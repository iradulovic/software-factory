<#
.SYNOPSIS
  Single entry point to start every Software Factory service for local desktop use (SF-615).

.DESCRIPTION
  Starts PostgreSQL (Docker Compose), then Factory.GitHubSync, Factory.Orchestrator, and Factory.Api on the host
  (not containerized: they need the operator's own Git config and authenticated gh/codex/claude CLI sessions),
  then the Factory.Web dashboard. Each service's stdout/stderr streams to its own file under logs/, and each
  process id started this run is recorded to scripts/.run/pids.json so stop.ps1 stops exactly what this script
  started - never a terminal the operator happened to have open separately.

  Existing database-tracked work needs no special handling here: the Orchestrator's own task leases (renewed
  every LeaseHeartbeatSeconds, default 120s) and the GitHub sync worker's idempotent publish-reconciliation
  already make a restart safe - a task an old process was mid-executing simply has its lease expire and gets
  reclaimed by the freshly started process, never duplicated. This script's job is only to get every process
  running again and to report clearly if one of them can't.

.PARAMETER StatusOnly
  Reports current health without starting anything - the right thing to run right after a reboot or a sleep/wake
  cycle to see what, if anything, actually needs restarting. Safe to run repeatedly.

.PARAMETER SkipDashboard
  Skips starting the Next.js dev server (useful when only the backend is needed).

.EXAMPLE
  ./scripts/start.ps1
.EXAMPLE
  ./scripts/start.ps1 -StatusOnly
#>
[CmdletBinding()]
param(
    [switch]$StatusOnly,
    [switch]$SkipDashboard
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$logsDir = Join-Path $repoRoot 'logs'
$runDir = Join-Path $PSScriptRoot '.run'
$pidFile = Join-Path $runDir 'pids.json'
$apiUrl = 'http://localhost:5080'
New-Item -ItemType Directory -Force -Path $logsDir, $runDir | Out-Null

# ---------------------------------------------------------------------------------------------
# Dedicated services worktree (SF-716) - every live service is started from a fixed worktree
# pinned to origin/main, never from $repoRoot directly. $repoRoot is the operator's own
# interactive checkout: an operator (or another Claude Code session) switching it to a feature
# branch to review or edit code must never change what the *running* services execute - that
# was a real bug (the ui/mono-theme-drift scenario: switching the shared checkout's branch
# silently changed the dashboard the running dev server served). Compose's project name is
# pinned explicitly to the shared checkout's own directory name so Postgres keeps using the
# exact same container/volume regardless of which worktree's docker-compose.yml started it.
# ---------------------------------------------------------------------------------------------
$servicesWorktreeDir = Join-Path $repoRoot '.worktrees/services'
$composeProjectName = Split-Path -Leaf $repoRoot
$composeFile = Join-Path $servicesWorktreeDir 'docker-compose.yml'

function Invoke-Native {
    # git and docker compose both write routine progress (git's "Preparing worktree...", compose's
    # "Container ... Running") to stderr on plain success; under this script's own
    # $ErrorActionPreference = 'Stop', PowerShell 5.1 promotes any stderr line from a native
    # command into a terminating NativeCommandError regardless of where it's redirected, even
    # though $LASTEXITCODE is 0. Only the *preference* being 'Stop' at the moment the command runs
    # causes the promotion, so relax it for the duration of the call (the function-local change
    # doesn't leak to the caller) and check the real exit code instead.
    param([string]$Exe, [string[]]$ExeArgs)
    $prevPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $output = & $Exe @ExeArgs 2>$null
    } finally {
        $ErrorActionPreference = $prevPreference
    }
    if ($LASTEXITCODE -ne 0) {
        & $Exe @ExeArgs
        throw "$Exe $($ExeArgs -join ' ') failed (exit $LASTEXITCODE)"
    }
    return $output
}

function Invoke-Git { Invoke-Native -Exe 'git' -ExeArgs $args }

function Sync-ServicesWorktree {
    Write-Section 'Dedicated services worktree (pinned to origin/main)'
    Push-Location $repoRoot
    try {
        Invoke-Git fetch origin main --quiet | Out-Null
        Invoke-Git worktree prune | Out-Null
        if (-not (Test-Path $servicesWorktreeDir)) {
            Invoke-Git worktree add --detach $servicesWorktreeDir origin/main | Out-Null
        } else {
            $resolved = (Resolve-Path -LiteralPath $servicesWorktreeDir).Path.Replace('\', '/')
            $worktrees = Invoke-Git worktree list --porcelain
            $registered = ($worktrees -join "`n").Replace('\', '/').Contains($resolved)
            if (-not $registered) {
                # Path exists but isn't a registered worktree (e.g. left over from a manual delete) - recreate cleanly.
                Remove-Item -Recurse -Force $servicesWorktreeDir
                Invoke-Git worktree add --detach $servicesWorktreeDir origin/main | Out-Null
            } else {
                # The worktree must always be disposable: discard any dirty tracked/untracked state
                # left behind (e.g. a stray manual edit) *before* switching commits, so a dirty
                # worktree can never abort the sync - only then check out and pin to origin/main.
                Invoke-Git -C $servicesWorktreeDir reset --hard --quiet | Out-Null
                Invoke-Git -C $servicesWorktreeDir clean -fd --quiet -e node_modules -e bin -e obj -e .next | Out-Null
                Invoke-Git -C $servicesWorktreeDir checkout --detach --quiet origin/main | Out-Null
                Invoke-Git -C $servicesWorktreeDir reset --hard --quiet origin/main | Out-Null
                Invoke-Git -C $servicesWorktreeDir clean -fd --quiet -e node_modules -e bin -e obj -e .next | Out-Null
            }
        }
    } finally {
        Pop-Location
    }
    $sha = (Invoke-Git -C $servicesWorktreeDir rev-parse --short HEAD).Trim()
    $subject = (Invoke-Git -C $servicesWorktreeDir log -1 --format='%s').Trim()
    Write-Ok "services worktree at $servicesWorktreeDir -> origin/main @ $sha ($subject)"
}

function Write-Section($text) { Write-Host "`n== $text ==" -ForegroundColor Cyan }
function Write-Ok($text) { Write-Host "  [OK]   $text" -ForegroundColor Green }
function Write-WarnLine($text) { Write-Host "  [WARN] $text" -ForegroundColor Yellow }
function Write-FailLine($text) { Write-Host "  [FAIL] $text" -ForegroundColor Red }

# npm's global executable shims are placed in its configured prefix on Windows. Ensure that
# directory is visible to the service processes started below, even when the launching shell's
# PATH does not already include it. This covers Pi and other globally installed CLI agents.
function Add-NpmGlobalPrefixToPath {
    $npmCommand = Get-Command 'npm.cmd' -ErrorAction SilentlyContinue
    if (-not $npmCommand) { $npmCommand = Get-Command 'npm' -ErrorAction SilentlyContinue }
    if (-not $npmCommand) { return }

    $previousPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $prefixOutput = & $npmCommand.Source config get prefix 2>$null
        $prefixExitCode = $LASTEXITCODE
    } finally {
        $ErrorActionPreference = $previousPreference
    }
    if ($prefixExitCode -ne 0) { return }

    $npmGlobalPrefix = [string]($prefixOutput | Select-Object -First 1)
    $npmGlobalPrefix = $npmGlobalPrefix.Trim()
    if (-not $npmGlobalPrefix -or -not (Test-Path -LiteralPath $npmGlobalPrefix -PathType Container)) { return }

    $alreadyOnPath = $false
    foreach ($entry in ($env:PATH -split [System.IO.Path]::PathSeparator)) {
        if ([string]::Equals($entry.Trim('"'), $npmGlobalPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
            $alreadyOnPath = $true
            break
        }
    }
    if (-not $alreadyOnPath) { $env:PATH = "$npmGlobalPrefix$([System.IO.Path]::PathSeparator)$env:PATH" }
}

Add-NpmGlobalPrefixToPath
. (Join-Path $PSScriptRoot 'cli-paths.ps1')
. (Join-Path $PSScriptRoot 'cli-auth.ps1')
Add-GrokBinToPath

# ---------------------------------------------------------------------------------------------
# CLI availability and authentication - checked here, proactively and visibly, rather than only
# discovered later as a confusing agent-process failure deep in a task's own log (SF-615's
# "missing authentication" acceptance criterion). Never blocks startup: a CLI issue here still
# lets Sync/Orchestrator/Api start (they degrade to leaving affected tasks NeedsHuman/blocked
# rather than crashing), it just gets called out clearly up front instead of silently.
# ---------------------------------------------------------------------------------------------
function Test-Cli {
    param([string]$Name, [string[]]$CheckArgs, [string]$Hint, [string[]]$FailureSignatures)
    $cmd = Get-Command $Name -ErrorAction SilentlyContinue
    if (-not $cmd) { Write-FailLine "$Name not found on PATH. $Hint"; return $false }
    try {
        $checkOutput = & $Name @CheckArgs 2>&1 | Out-String
        if (Test-AuthenticationProbe -Succeeded ($LASTEXITCODE -eq 0) -StandardOutput $checkOutput -FailureSignatures $FailureSignatures) {
            Write-Ok "$Name is installed and responding"; return $true
        }
        if ($LASTEXITCODE -eq 0) { Write-WarnLine "$Name reported an authentication failure. $Hint"; return $false }
        Write-WarnLine "$Name is installed but '$Name $($CheckArgs -join ' ')' exited $LASTEXITCODE - it may need authentication ($Hint)."
        return $false
    } catch {
        Write-WarnLine "$Name check failed: $_"
        return $false
    }
}

Write-Section 'CLI availability and authentication'
Test-Cli -Name 'gh' -CheckArgs @('auth', 'status') -Hint 'Run "gh auth login".' | Out-Null
Test-Cli -Name 'codex' -CheckArgs @('--version') -Hint 'Run "codex login".' | Out-Null
Test-Cli -Name 'claude' -CheckArgs @('--version') -Hint 'Run "claude login" (or sign in on first use).' | Out-Null
Test-Cli -Name 'pi' -CheckArgs @('--version') -Hint 'Install with "npm install -g --ignore-scripts @earendil-works/pi-coding-agent" and sign in for the configured model.' | Out-Null
$grokProfileSettings = (Get-Content -LiteralPath (Join-Path $repoRoot 'src/Factory.Orchestrator/appsettings.json') -Raw | ConvertFrom-Json).Agents.Profiles | Where-Object { $_.Name -eq 'Grok' }
Test-Cli -Name 'grok' -CheckArgs @('models') -FailureSignatures $grokProfileSettings.AuthenticationFailureSignatures -Hint 'Install Grok Build from https://x.ai/cli and run "grok login".' | Out-Null
Test-Cli -Name 'docker' -CheckArgs @('version', '--format', '{{.Server.Version}}') -Hint 'Start Docker Desktop.' | Out-Null

function Get-ApiHealth {
    try {
        $response = Invoke-WebRequest -Uri "$apiUrl/health" -UseBasicParsing -TimeoutSec 3
        return ($response.StatusCode -eq 200), $response.Content
    } catch {
        return $false, $_.Exception.Message
    }
}

function Get-RecordedPids {
    if (Test-Path $pidFile) { return Get-Content $pidFile -Raw | ConvertFrom-Json }
    return $null
}

function Show-Status {
    Write-Section 'Recorded processes (this script''s own last run)'
    $recorded = Get-RecordedPids
    if (-not $recorded) {
        Write-WarnLine 'No scripts/.run/pids.json found - either never started via this script, or stop.ps1 already cleaned it up.'
    } else {
        foreach ($entry in $recorded.PSObject.Properties) {
            $procId = $entry.Value
            $proc = Get-Process -Id $procId -ErrorAction SilentlyContinue
            if ($proc) { Write-Ok "$($entry.Name): running (pid $procId)" }
            else { Write-FailLine "$($entry.Name): not running (last known pid $procId) - see logs/$($entry.Name).err.log" }
        }
    }

    Write-Section 'Dedicated services worktree (SF-716 - what''s actually running)'
    if (Test-Path $servicesWorktreeDir) {
        $sha = (git -C $servicesWorktreeDir rev-parse --short HEAD).Trim()
        $subject = (git -C $servicesWorktreeDir log -1 --format='%s').Trim()
        Write-Ok "$servicesWorktreeDir @ $sha ($subject)"
    } else {
        Write-WarnLine "$servicesWorktreeDir does not exist yet - run ./scripts/start.ps1 (without -StatusOnly) to create it."
    }
    $shared = (git -C $repoRoot branch --show-current).Trim()
    Write-Ok "shared interactive checkout ($repoRoot) is on '$shared' - irrelevant to what's running, shown for reference only"

    Write-Section 'PostgreSQL (Docker Compose)'
    $pg = docker compose -f $composeFile -p $composeProjectName ps postgres --format json 2>$null
    if ($pg) { Write-Ok 'postgres container reported by Docker Compose' } else { Write-FailLine 'postgres container not found - run docker compose up -d postgres' }

    Write-Section 'Factory.Api health (actually checks database connectivity, not just process liveness)'
    $healthy, $body = Get-ApiHealth
    if ($healthy) { Write-Ok "GET $apiUrl/health -> $body" }
    else { Write-FailLine "GET $apiUrl/health failed: $body" }

    Write-Section 'Worker liveness (factory.worker heartbeats, via the API)'
    try {
        $workers = Invoke-RestMethod -Uri "$apiUrl/api/workers" -TimeoutSec 3
        if (-not $workers -or $workers.Count -eq 0) { Write-WarnLine 'No worker has ever heartbeated yet.' }
        foreach ($w in $workers) {
            $label = if ($w.isStale) { 'STALE' } else { 'fresh' }
            $task = if ($w.currentTaskTitle) { "task: $($w.currentTaskTitle)" } else { 'idle' }
            if ($w.isStale) { Write-WarnLine "$($w.workerId) on $($w.host): $label, last seen $($w.lastSeenAt) ($task)" }
            else { Write-Ok "$($w.workerId) on $($w.host): $label, last seen $($w.lastSeenAt) ($task)" }
        }
    } catch {
        Write-FailLine "Could not reach $apiUrl/api/workers: $_"
    }
}

if ($StatusOnly) {
    Show-Status
    Write-Host "`nStatus check complete. Nothing was started." -ForegroundColor Cyan
    return
}

Sync-ServicesWorktree

# ---------------------------------------------------------------------------------------------
# PostgreSQL - started from the dedicated worktree's docker-compose.yml (same pin as every other
# service), with the Compose project name fixed to the shared checkout's directory name so this
# always resolves to the exact same container/volume regardless of which worktree provided the
# compose file.
# ---------------------------------------------------------------------------------------------
Write-Section 'PostgreSQL'
Push-Location $servicesWorktreeDir
try {
    Invoke-Native -Exe 'docker' -ExeArgs @('compose', '-f', $composeFile, '-p', $composeProjectName, 'up', '-d', 'postgres') | Out-Null
    $deadline = (Get-Date).AddSeconds(60)
    $healthy = $false
    while ((Get-Date) -lt $deadline) {
        $status = docker compose -f $composeFile -p $composeProjectName ps postgres --format json 2>$null | ConvertFrom-Json -ErrorAction SilentlyContinue
        if ($status -and $status.Health -eq 'healthy') { $healthy = $true; break }
        Start-Sleep -Seconds 2
    }
    if ($healthy) { Write-Ok 'postgres is healthy' } else { Write-WarnLine 'postgres did not report healthy within 60s - continuing anyway; check "docker compose logs postgres".' }
} finally {
    Pop-Location
}

# ---------------------------------------------------------------------------------------------
# Build once, synchronously, before starting any service - Sync/Orchestrator/Api share several
# project references (Factory.Core, Factory.Infrastructure). Launching three concurrent
# `dotnet run` processes against a worktree with no prior build output makes each one try to
# build those shared projects at once, and MSBuild's concurrent writes to the same obj/ output
# race and fail (observed live the first time the dedicated worktree was created: Sync's build
# failed outright, and Api's Kestrel bound port 5080 then crashed shortly after with
# "address already in use" from a second, concurrently-building instance). A warm build already
# on disk (the common case after the very first run, since bin/obj survive Sync-ServicesWorktree's
# cleanup) makes this a fast no-op; it only costs real time on a brand-new worktree.
# ---------------------------------------------------------------------------------------------
Write-Section 'Building services worktree'
Invoke-Native -Exe 'dotnet' -ExeArgs @('build', $servicesWorktreeDir, '--nologo') | Out-Null
Write-Ok 'dotnet build succeeded'

# ---------------------------------------------------------------------------------------------
# .NET services - each runs from the dedicated services worktree (SF-716), never from $repoRoot,
# so RootDirectory/LogsDirectory (both configured as relative paths) always resolve to one
# consistent place tied to origin/main, regardless of what branch an operator happens to have
# the shared interactive checkout on, or which project's own subdirectory they might otherwise
# have `cd`-ed into (a real footgun: a `dotnet run` invoked from inside src/Factory.Orchestrator
# lands its factory-data/ there instead of at the repo root, silently splitting state across
# three separate copies).
# ---------------------------------------------------------------------------------------------
function Start-DotnetService {
    param([string]$Name, [string]$Project, [string[]]$ExtraArgs = @())
    $log = Join-Path $logsDir "$Name.log"
    $errLog = Join-Path $logsDir "$Name.err.log"
    $arguments = @('run', '--project', $Project) + $ExtraArgs
    $proc = Start-Process -FilePath 'dotnet' -ArgumentList $arguments -WorkingDirectory $servicesWorktreeDir `
        -RedirectStandardOutput $log -RedirectStandardError $errLog -PassThru -WindowStyle Hidden
    Write-Ok "$Name started (pid $($proc.Id)) - stdout/stderr: $log / $errLog"
    return $proc.Id
}

Write-Section '.NET services'
$recordedPids = @{}
$recordedPids['sync'] = Start-DotnetService -Name 'sync' -Project 'src/Factory.GitHubSync'
$recordedPids['orchestrator'] = Start-DotnetService -Name 'orchestrator' -Project 'src/Factory.Orchestrator'
$recordedPids['api'] = Start-DotnetService -Name 'api' -Project 'src/Factory.Api' -ExtraArgs @('--urls', $apiUrl)

if (-not $SkipDashboard) {
    Write-Section 'Dashboard (Factory.Web)'
    $webDir = Join-Path $servicesWorktreeDir 'web/Factory.Web'
    $npmCmd = (Get-Command npm.cmd -ErrorAction SilentlyContinue).Source
    if (-not $npmCmd) { $npmCmd = (Get-Command npm -ErrorAction SilentlyContinue).Source }
    if (-not (Test-Path (Join-Path $webDir 'node_modules'))) {
        # The dedicated worktree (SF-716) is a fresh checkout every time it's (re)created, so this
        # runs on the worktree's first use even if an operator's own checkout already has
        # node_modules elsewhere - node_modules is never git-tracked, so worktrees don't share it.
        Write-WarnLine 'node_modules not found in the services worktree - running npm install first (one-time).'
        Push-Location $webDir
        try { Invoke-Native -Exe $npmCmd -ExeArgs @('install') | Out-Null } finally { Pop-Location }
    }
    $log = Join-Path $logsDir 'dashboard.log'
    $errLog = Join-Path $logsDir 'dashboard.err.log'
    $proc = Start-Process -FilePath $npmCmd -ArgumentList @('run', 'dev') -WorkingDirectory $webDir `
        -RedirectStandardOutput $log -RedirectStandardError $errLog -PassThru -WindowStyle Hidden
    Write-Ok "dashboard started (pid $($proc.Id)) - stdout/stderr: $log / $errLog"
    $recordedPids['dashboard'] = $proc.Id
}

$recordedPids | ConvertTo-Json | Set-Content -Path $pidFile
Write-Ok "Process ids recorded to $pidFile"

# ---------------------------------------------------------------------------------------------
# Wait for the API to actually answer, then print a final status summary - so a startup failure
# is visible and actionable right here, not discovered later when the dashboard shows nothing.
# ---------------------------------------------------------------------------------------------
Write-Section 'Waiting for Factory.Api to become healthy'
$deadline = (Get-Date).AddSeconds(45)
$healthy = $false
while ((Get-Date) -lt $deadline) {
    $healthy, $body = Get-ApiHealth
    if ($healthy) { break }
    Start-Sleep -Seconds 2
}
if ($healthy) { Write-Ok "Factory.Api is healthy: $body" }
else { Write-FailLine "Factory.Api did not become healthy within 45s. Check logs/api.err.log and logs/api.log." }

Show-Status

Write-Host "`nDashboard: http://localhost:3000" -ForegroundColor Cyan
Write-Host "API:       $apiUrl" -ForegroundColor Cyan
Write-Host "Logs:      $logsDir" -ForegroundColor Cyan
Write-Host "Stop with: ./scripts/stop.ps1" -ForegroundColor Cyan
