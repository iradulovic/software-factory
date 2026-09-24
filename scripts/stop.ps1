<#
.SYNOPSIS
  Stops exactly the processes scripts/start.ps1 started (SF-615), and nothing else.

.DESCRIPTION
  Reads scripts/.run/pids.json (written by start.ps1), stops any of those process ids that are still running,
  and removes the file. Never touches a terminal the operator started by hand, and never force-stops PostgreSQL's
  container unless -StopPostgres is passed - the database and its data should normally keep running (or be
  restarted independently) so other work isn't disrupted by stopping the app services.

.PARAMETER StopPostgres
  Also stops (not removes) the PostgreSQL container via docker compose. Data is preserved either way (the
  Compose volume is untouched); this only stops the running container.

.EXAMPLE
  ./scripts/stop.ps1
.EXAMPLE
  ./scripts/stop.ps1 -StopPostgres
#>
[CmdletBinding()]
param(
    [switch]$StopPostgres
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$runDir = Join-Path $PSScriptRoot '.run'
$pidFile = Join-Path $runDir 'pids.json'
# Same dedicated worktree/Compose pin as start.ps1 (SF-716) - stopping Postgres must target the
# exact container start.ps1 started, not a differently-named project derived from $repoRoot.
$servicesWorktreeDir = Join-Path $repoRoot '.worktrees/services'
$composeProjectName = Split-Path -Leaf $repoRoot
$composeFile = Join-Path $servicesWorktreeDir 'docker-compose.yml'
if (-not (Test-Path $composeFile)) { $composeFile = Join-Path $repoRoot 'docker-compose.yml' }

function Write-Ok($text) { Write-Host "  [OK]   $text" -ForegroundColor Green }
function Write-WarnLine($text) { Write-Host "  [WARN] $text" -ForegroundColor Yellow }

function Invoke-Native {
    # docker compose writes routine progress ("Container ... Stopping") to stderr on plain
    # success; under this script's own $ErrorActionPreference = 'Stop', PowerShell 5.1 promotes
    # any stderr line from a native command into a terminating NativeCommandError regardless of
    # where it's redirected, even though $LASTEXITCODE is 0. Relax the preference for the
    # duration of the call (the function-local change doesn't leak to the caller) and check the
    # real exit code instead.
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

if (-not (Test-Path $pidFile)) {
    Write-WarnLine "No $pidFile found - nothing recorded to stop. If services are still running, they weren't started by start.ps1, or were already stopped."
} else {
    $recorded = Get-Content $pidFile -Raw | ConvertFrom-Json
    foreach ($entry in $recorded.PSObject.Properties) {
        $procId = $entry.Value
        $proc = Get-Process -Id $procId -ErrorAction SilentlyContinue
        if (-not $proc) { Write-WarnLine "$($entry.Name): pid $procId already not running"; continue }
        Stop-Process -Id $procId -Force
        Write-Ok "$($entry.Name): stopped (pid $procId)"
    }
    Remove-Item $pidFile -Force
}

if ($StopPostgres) {
    Invoke-Native -Exe 'docker' -ExeArgs @('compose', '-f', $composeFile, '-p', $composeProjectName, 'stop', 'postgres') | Out-Null
    Write-Ok 'postgres container stopped (data volume preserved)'
}
