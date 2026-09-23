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

function Write-Section($text) { Write-Host "`n== $text ==" -ForegroundColor Cyan }
function Write-Ok($text) { Write-Host "  [OK]   $text" -ForegroundColor Green }
function Write-WarnLine($text) { Write-Host "  [WARN] $text" -ForegroundColor Yellow }
function Write-FailLine($text) { Write-Host "  [FAIL] $text" -ForegroundColor Red }

# ---------------------------------------------------------------------------------------------
# CLI availability and authentication - checked here, proactively and visibly, rather than only
# discovered later as a confusing agent-process failure deep in a task's own log (SF-615's
# "missing authentication" acceptance criterion). Never blocks startup: a CLI issue here still
# lets Sync/Orchestrator/Api start (they degrade to leaving affected tasks NeedsHuman/blocked
# rather than crashing), it just gets called out clearly up front instead of silently.
# ---------------------------------------------------------------------------------------------
function Test-Cli {
    param([string]$Name, [string[]]$CheckArgs, [string]$Hint)
    $cmd = Get-Command $Name -ErrorAction SilentlyContinue
    if (-not $cmd) { Write-FailLine "$Name not found on PATH. $Hint"; return $false }
    try {
        $null = & $Name @CheckArgs 2>&1
        if ($LASTEXITCODE -eq 0) { Write-Ok "$Name is installed and responding"; return $true }
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

    Write-Section 'PostgreSQL (Docker Compose)'
    $pg = docker compose -f (Join-Path $repoRoot 'docker-compose.yml') ps postgres --format json 2>$null
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

# ---------------------------------------------------------------------------------------------
# PostgreSQL
# ---------------------------------------------------------------------------------------------
Write-Section 'PostgreSQL'
Push-Location $repoRoot
try {
    docker compose up -d postgres
    $deadline = (Get-Date).AddSeconds(60)
    $healthy = $false
    while ((Get-Date) -lt $deadline) {
        $status = docker compose ps postgres --format json 2>$null | ConvertFrom-Json -ErrorAction SilentlyContinue
        if ($status -and $status.Health -eq 'healthy') { $healthy = $true; break }
        Start-Sleep -Seconds 2
    }
    if ($healthy) { Write-Ok 'postgres is healthy' } else { Write-WarnLine 'postgres did not report healthy within 60s - continuing anyway; check "docker compose logs postgres".' }
} finally {
    Pop-Location
}

# ---------------------------------------------------------------------------------------------
# .NET services - each runs from the repo root explicitly so RootDirectory/LogsDirectory (both
# configured as relative paths) always resolve to the same place regardless of which project's
# own subdirectory an operator might otherwise have `cd`-ed into (a real footgun today: a
# `dotnet run` invoked from inside src/Factory.Orchestrator lands its factory-data/ there
# instead of at the repo root, silently splitting state across three separate copies).
# ---------------------------------------------------------------------------------------------
function Start-DotnetService {
    param([string]$Name, [string]$Project, [string[]]$ExtraArgs = @())
    $log = Join-Path $logsDir "$Name.log"
    $errLog = Join-Path $logsDir "$Name.err.log"
    $arguments = @('run', '--project', $Project) + $ExtraArgs
    $proc = Start-Process -FilePath 'dotnet' -ArgumentList $arguments -WorkingDirectory $repoRoot `
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
    $webDir = Join-Path $repoRoot 'web/Factory.Web'
    if (-not (Test-Path (Join-Path $webDir 'node_modules'))) {
        Write-WarnLine 'node_modules not found - running npm install first (one-time).'
        Push-Location $webDir
        try { npm install } finally { Pop-Location }
    }
    $log = Join-Path $logsDir 'dashboard.log'
    $errLog = Join-Path $logsDir 'dashboard.err.log'
    $npmCmd = (Get-Command npm.cmd -ErrorAction SilentlyContinue).Source
    if (-not $npmCmd) { $npmCmd = (Get-Command npm -ErrorAction SilentlyContinue).Source }
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
