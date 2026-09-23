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

function Write-Ok($text) { Write-Host "  [OK]   $text" -ForegroundColor Green }
function Write-WarnLine($text) { Write-Host "  [WARN] $text" -ForegroundColor Yellow }

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
    Push-Location $repoRoot
    try {
        docker compose stop postgres
        Write-Ok 'postgres container stopped (data volume preserved)'
    } finally {
        Pop-Location
    }
}
