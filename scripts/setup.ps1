<#
.SYNOPSIS
  Guided, repeatable Windows setup for Software Factory.
.EXAMPLE
  ./scripts/setup.ps1
.EXAMPLE
  ./scripts/setup.ps1 -CheckOnly
#>
[CmdletBinding()]
param(
    [switch]$CheckOnly,
    [switch]$Repair,
    [string]$Repository,
    [string]$DefaultBranch = 'main'
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'setup.lib.ps1')
. (Join-Path $PSScriptRoot 'cli-paths.ps1')
Add-GrokBinToPath

if ($Repository -and $Repository -notmatch '^[a-zA-Z0-9._-]+/[a-zA-Z0-9._-]+$') {
    throw 'Repository must be OWNER/NAME using letters, numbers, periods, underscores, or hyphens.'
}
if ($DefaultBranch -notmatch '^[a-zA-Z0-9._/-]+$' -or $DefaultBranch.StartsWith('-') -or $DefaultBranch.Contains('..')) {
    throw 'DefaultBranch contains unsupported characters.'
}

$report = [ordered]@{ checkedAt = (Get-Date).ToUniversalTime().ToString('o'); prerequisites = @(); doctor = @(); ready = $false }
function Add-Result($Name, $State, $Detail, $Action) {
    $report.prerequisites += [pscustomobject]@{ name = $Name; state = $State; detail = $Detail; action = $Action }
    Write-Host ('  [{0}] {1}: {2}' -f $State, $Name, $Detail)
    if ($Action) { Write-Host ('      Next: {0}' -f $Action) }
}
function Save-Report {
    $dir = Join-Path $repoRoot 'logs'
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    $path = Join-Path $dir 'setup-report.json'
    # Only statuses and fixed remediation text are saved. Never save command output, environment, or auth details.
    $report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $path -Encoding UTF8
    Write-Host "Report: $path"
}

try {
    Write-Host "`nSoftware Factory setup - $repoRoot"
    $win = Get-WindowsReadiness
    Add-Result 'Windows' $win.State $win.Detail $win.Action
    $shell = Get-ShellReadiness
    Add-Result 'PowerShell' $shell.State $shell.Detail $shell.Action
    $wsl = Get-WslReadiness
    Add-Result 'Virtualization / WSL' $wsl.State $wsl.Detail $wsl.Action

    $tools = @(
        @{ Name='Git'; Exe='git'; Args=@('--version'); Minimum=0; Package='Git.Git'; Action='Install Git.Git with winget.' },
        @{ Name='GitHub CLI'; Exe='gh'; Args=@('--version'); Minimum=0; Package='GitHub.cli'; Action='Install GitHub.cli with winget.' },
        @{ Name='.NET SDK 10'; Exe='dotnet'; Args=@('--list-sdks'); Minimum=10; Package='Microsoft.DotNet.SDK.10'; Action='Install Microsoft.DotNet.SDK.10 with winget.' },
        @{ Name='Node.js 24+'; Exe='node'; Args=@('--version'); Minimum=24; Package='OpenJS.NodeJS'; Action='Install Node.js 24 or newer from OpenJS.NodeJS with winget.' },
        @{ Name='npm'; Exe='npm.cmd'; Args=@('--version'); Minimum=0; Package=''; Action='Repair the Node.js installation so npm is on PATH.' }
    )
    $inventory = @()
    $coreUnready = @()
    foreach ($tool in $tools) {
        $result = Test-Prerequisite $tool
        $inventory += [pscustomobject]@{ Tool=$tool; Result=$result }
        Add-Result $tool.Name $result.State $result.Detail $result.Action
        if ($result.State -ne 'ready') { $coreUnready += $tool.Name }
    }
    $missing = @(Get-InstallCandidates $inventory)
    $runtime = Get-ContainerReadiness -RepoRoot $repoRoot
    Add-Result 'Docker daemon and Compose' $runtime.State $runtime.Detail $runtime.Action
    if ($runtime.State -ne 'ready') {
        Add-Result 'Docker Desktop' 'operator' 'Preferred Windows container runtime; an existing compatible daemon is accepted.' 'Install Docker Desktop per-user with the WSL 2 backend, start it, accept its license, then rerun. See docs/first-run.md.'
    }
    $profiles = Get-AgentProfiles -RepoRoot $repoRoot
    $readyAgents = 0
    foreach ($profile in $profiles) {
        $agent = Test-AgentProfile $profile
        Add-Result "Agent $($profile.Name)" $agent.State $agent.Detail $agent.Action
        if ($agent.State -eq 'ready') { $readyAgents++ }
    }
    $browserConfig = Join-Path $repoRoot '.factory/config.json'
    if (Test-Path $browserConfig) {
        $config = Get-Content $browserConfig -Raw | ConvertFrom-Json
        if ($config.smokeTest) {
            foreach ($commandName in @('installCommand', 'startCommand')) {
                $commandLine = $config.smokeTest.$commandName
                if ($commandLine -is [array] -and $commandLine.Count -gt 0) {
                    $executable = [string]$commandLine[0]
                    if (Get-Command $executable -ErrorAction SilentlyContinue) {
                        Add-Result "Smoke test $commandName" 'ready' 'Configured executable is present.' ''
                    } else {
                        Add-Result "Smoke test $commandName" 'operator' 'Configured executable is missing.' "Update the target repository .factory/config.json $commandName executable or install it at its configured path."
                    }
                }
            }
            $playwrightRoot = if ($env:PLAYWRIGHT_BROWSERS_PATH) { $env:PLAYWRIGHT_BROWSERS_PATH } else { Join-Path $env:LOCALAPPDATA 'ms-playwright' }
            $chromium = @(Get-ChildItem -Path $playwrightRoot -Directory -Filter 'chromium-*' -ErrorAction SilentlyContinue).Count -gt 0
            if ($chromium) { Add-Result 'Browser smoke test' 'ready' 'Playwright Chromium cache is present.' '' }
            else { Add-Result 'Browser smoke test' 'operator' 'Playwright Chromium is needed when smokeTest is enabled in the target repository.' 'Run playwright install chromium on the machine executing smoke tests.' }
        }
        $merge = if ($config.requireHumanMerge -eq $false) { 'automatic after CI when the task policy permits' } else { 'human review' }
        Write-Host "`nThis repository policy: publish=$($config.publish); merge=$merge. HUMAN REVIEW on an issue always requires human merge."
    }
    if ($Repository) {
        Write-Host "Target repository $Repository uses its own default-branch .factory/config.json. Without it, publication is manual and merge requires human review. Inspect that file before labeling an issue factory:ready."
    }
    Add-Result 'Backup / restore' 'ready' 'pg_dump and pg_restore run inside the Compose PostgreSQL container.' ''
    $dataRoot = if ($env:Factory__RootDirectory) { $env:Factory__RootDirectory } else { Join-Path $repoRoot '.worktrees/services/factory-data' }
    Write-Host "Workspace data location: $dataRoot"
    if (Test-Path -LiteralPath $dataRoot -PathType Leaf) {
        Add-Result 'Workspace and data' 'operator' 'Factory:RootDirectory points to a file.' 'Set Factory__RootDirectory to a directory and rerun.'
        $coreUnready += 'Workspace and data'
    } else {
        Add-Result 'Workspace and data' 'ready' 'Configured data directory is usable or will be created by startup.' ''
    }

    Write-Host "`nInstall plan (winget source, exact package IDs; no upgrade on rerun):"
    foreach ($tool in $missing) {
        Write-Host "  $($tool.Name): winget install --exact --id $($tool.Package) --source winget --scope user"
        if (-not $CheckOnly -and (Get-Command winget -ErrorAction SilentlyContinue)) {
            Write-Host '  Available package metadata (review Version and Source before agreeing):'
            & winget show --exact --id $tool.Package --source winget --accept-source-agreements
            if ($LASTEXITCODE -ne 0) { throw "Could not resolve $($tool.Package) in winget. Install it from its official vendor and rerun." }
        }
    }
    if ($Repair) {
        foreach ($tool in $tools | Where-Object { $_.Package -and $_ -notin $missing }) {
            Write-Host "  winget upgrade --exact --id $($tool.Package) --source winget --scope user  (explicit repair/upgrade)"
        }
    }
    $installedNow = $false
    if ($missing.Count -gt 0 -and -not $CheckOnly) {
        $answer = Read-Host 'Install the listed packages now? [y/N]'
        if ($answer -eq 'y') {
            if (-not (Get-Command winget -ErrorAction SilentlyContinue)) { throw 'winget is unavailable. Install App Installer from Microsoft, then rerun.' }
            foreach ($tool in $missing) {
                $args = Get-WingetInstallArgs $tool.Package
                Write-Host "Installing $($tool.Package) from winget. A package may request a scoped elevation prompt."
                & winget @args
                if ($LASTEXITCODE -ne 0) { throw "Install of $($tool.Package) failed (exit $LASTEXITCODE). Rerun setup after resolving it." }
                $installedNow = $true
            }
            Write-Host 'Open a new terminal so PATH changes take effect, then rerun setup.'
        }
    }
    if ($Repair -and -not $CheckOnly) {
        $answer = Read-Host 'Upgrade already-installed packages listed above? [y/N]'
        if ($answer -eq 'y') {
            if (-not (Get-Command winget -ErrorAction SilentlyContinue)) { throw 'winget is unavailable. Install App Installer from Microsoft, then rerun.' }
            foreach ($tool in $tools | Where-Object { $_.Package -and $_ -notin $missing }) {
                & winget upgrade --exact --id $tool.Package --source winget --scope user --accept-source-agreements --accept-package-agreements
                if ($LASTEXITCODE -ne 0) { throw "Upgrade of $($tool.Package) failed (exit $LASTEXITCODE)." }
            }
        }
    }
    if ($CheckOnly) {
        Write-Host "`nCheck-only: no installation, sign-in, service start, or repository changes."
    } elseif ($installedNow) {
        Write-Host 'Open a new non-admin terminal and rerun setup to refresh PATH and validate installed versions.'
    } elseif ($coreUnready.Count -gt 0) {
        Write-Host "Resolve required prerequisites ($($coreUnready -join ', ')) before starting services, then rerun setup."
    } elseif ($readyAgents -eq 0) {
        Write-Host 'Install and sign in to at least one configured agent before starting services.'
    } else {
        Write-Host "`nSign in in this non-admin user session: gh auth login -h github.com; codex login; claude login; pi auth check --model moonshotai/kimi-k2.6 --json (if Pi is used); grok login (if Grok is used)."
        if ((Read-Host 'Have you completed the required sign-ins and started the container daemon? [y/N]') -eq 'y') {
            $runtime = Get-ContainerReadiness -RepoRoot $repoRoot
            if ($runtime.State -ne 'ready') { throw $runtime.Action }
            if ((Read-Host 'Start local services with scripts/start.ps1? [y/N]') -eq 'y') {
                Test-ServicesWorktreeSafe -RepoRoot $repoRoot
                & (Join-Path $PSScriptRoot 'start.ps1')
                if (-not $?) { throw 'start.ps1 failed. Inspect logs/ and rerun setup.' }
            }
        }
    }

    $doctor = Get-SetupDoctor -RepoRoot $repoRoot
    foreach ($item in $doctor) {
        $report.doctor += $item
        Write-Host ('  [{0}] {1}: {2}' -f $item.state, $item.name, $item.detail)
    }
    $report.ready = (@($report.doctor | Where-Object { $_.state -ne 'ready' }).Count -eq 0) -and ($coreUnready.Count -eq 0) -and ($readyAgents -gt 0)
    if ($readyAgents -eq 0) { Write-Host 'At least one configured coding agent must respond and be signed in.' }
    if ($Repository -and -not $CheckOnly -and $report.ready) {
        Write-Host "Repository registration requested: $Repository ($DefaultBranch)."
        if ((Read-Host 'Register it through the local API? [y/N]') -eq 'y') {
            Register-FactoryRepository -Repository $Repository -DefaultBranch $DefaultBranch
        }
    }
    if ($report.ready) { Write-Host 'Ready. For a safe test, follow docs/first-run.md and put HUMAN REVIEW on the issue before factory:ready.' }
    else { Write-Host 'Setup needs attention. Resolve the lines above and rerun this wizard.' }
} catch {
    Write-Host "Setup stopped: $($_.Exception.Message)" -ForegroundColor Red
    $report.doctor += [pscustomobject]@{ name='setup'; state='operator'; detail='Setup stopped; inspect the terminal message and rerun.' }
} finally {
    Save-Report
}
if (-not $report.ready) { exit 1 }
