<#
.SYNOPSIS
  Back up PostgreSQL state, repository caches, and worktrees (unpushed branches and uncommitted work) (SF-616).

.DESCRIPTION
  GitHub alone is not a backup: a task's database row, its dependency graph, and any work an agent committed to a
  branch that was never pushed (or never even committed) live only in this machine's PostgreSQL volume and
  factory-data/ tree. This script captures both, consistently, into one timestamped snapshot directory:

    <Destination>/<yyyyMMdd-HHmmss>/database.dump     - pg_dump custom-format dump of the whole database
    <Destination>/<yyyyMMdd-HHmmss>/factory-data/      - full copy of RootDirectory (repositories/ + worktrees/)
    <Destination>/<yyyyMMdd-HHmmss>/manifest.json      - snapshot metadata used by restore.ps1's own reporting

  Quiescence: dispatch is paused (SF-610's global pause, via the API) before the snapshot and this script waits
  for any already-executing tasks to finish, so the worktree copy isn't racing an agent's own writes. If dispatch
  was already paused for some other reason, that pause is left in place; otherwise the pause this script set is
  lifted again once the snapshot completes. The API doesn't need to be running at all - if it can't be reached
  (e.g. taking a backup from a cold, fully-stopped desktop), quiescence is skipped and it says so; the dump itself
  is always transactionally consistent either way.

  Credential handling: the dump and the copied factory-data/ tree contain factory task state and this machine's
  own Git history - no CLI credentials. `gh`/`codex`/`claude` authentication lives in the interactive user's own
  profile (outside RootDirectory) and is never touched by, or included in, this backup. The PostgreSQL connection
  itself uses the local trust-authenticated Docker Compose Postgres (no password leaves this machine); nothing
  here needs a secret to run. Treat the backup destination itself as sensitive: anyone with access to a snapshot
  can read every task's implementation history and diff.

  Retention: after a successful snapshot, only the newest -RetentionCount snapshot directories under -Destination
  are kept; older ones are deleted. Pass -RetentionCount 0 to disable pruning.

.PARAMETER Destination
  Where snapshot directories are written. Defaults to $env:FACTORY_BACKUP_DIR, or ./backups under the repo root.
  Point this at removable/network storage for real disaster recovery - a backup that lives on the same disk as
  the thing it backs up only protects against database or worktree corruption, not drive loss.

.PARAMETER RetentionCount
  How many snapshots to keep under -Destination. Defaults to $env:FACTORY_BACKUP_RETENTION, or 14. 0 disables pruning.

.PARAMETER RootDirectory
  The factory state directory to copy (repositories/ + worktrees/). Defaults to $env:Factory__RootDirectory, or
  ./factory-data under the repo root (this repo's own configured default; see Factory:RootDirectory in appsettings).

.PARAMETER DatabaseName
  Database to dump. Defaults to software_factory (this repo's Docker Compose default).

.PARAMETER PostgresUser
  Postgres role used for pg_dump/psql, run inside the Compose postgres container. Defaults to factory.

.PARAMETER ApiUrl
  Factory.Api base URL used to pause dispatch and wait for quiescence. Defaults to http://localhost:5080.

.PARAMETER QuiesceTimeoutSeconds
  How long to wait for active tasks to drain after pausing before snapshotting anyway. Defaults to 120.

.PARAMETER SkipQuiesce
  Skip the pause/wait-for-drain step entirely (still produces a consistent dump; only the worktree copy could then
  race an in-flight agent write). Use for a backup taken while the factory is already known to be stopped.

.EXAMPLE
  ./scripts/backup.ps1
.EXAMPLE
  ./scripts/backup.ps1 -Destination D:\factory-backups -RetentionCount 30
#>
[CmdletBinding()]
param(
    [string]$Destination = $(if ($env:FACTORY_BACKUP_DIR) { $env:FACTORY_BACKUP_DIR } else { Join-Path (Split-Path -Parent $PSScriptRoot) 'backups' }),
    [int]$RetentionCount = $(if ($env:FACTORY_BACKUP_RETENTION) { [int]$env:FACTORY_BACKUP_RETENTION } else { 14 }),
    [string]$RootDirectory = $(if ($env:Factory__RootDirectory) { $env:Factory__RootDirectory } else { Join-Path (Split-Path -Parent $PSScriptRoot) 'factory-data' }),
    [string]$DatabaseName = 'software_factory',
    [string]$PostgresUser = 'factory',
    [string]$ApiUrl = 'http://localhost:5080',
    [int]$QuiesceTimeoutSeconds = 120,
    [switch]$SkipQuiesce
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$composeFile = Join-Path $repoRoot 'docker-compose.yml'
$timestamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$snapshotDir = Join-Path $Destination $timestamp

function Write-Section($text) { Write-Host "`n== $text ==" -ForegroundColor Cyan }
function Write-Ok($text) { Write-Host "  [OK]   $text" -ForegroundColor Green }
function Write-WarnLine($text) { Write-Host "  [WARN] $text" -ForegroundColor Yellow }
function Write-FailLine($text) { Write-Host "  [FAIL] $text" -ForegroundColor Red }

# ---------------------------------------------------------------------------------------------
# Quiescence - best-effort. A missing API just means "can't confirm nothing's running"; the dump
# is consistent regardless, so this never blocks taking a backup.
# ---------------------------------------------------------------------------------------------
$weSetThePause = $false
if (-not $SkipQuiesce) {
    Write-Section 'Quiescing dispatch (SF-610 pause)'
    try {
        $pauses = Invoke-RestMethod -Uri "$ApiUrl/api/control/pause" -TimeoutSec 5
        $alreadyPaused = @($pauses | Where-Object { $_.scope -eq 'Global' -and $_.paused }).Count -gt 0
        if ($alreadyPaused) {
            Write-Ok 'Dispatch is already paused - leaving that pause in place.'
        } else {
            Invoke-RestMethod -Uri "$ApiUrl/api/control/pause" -Method Post -ContentType 'application/json' `
                -Body (@{ reason = "Backup snapshot $timestamp" } | ConvertTo-Json) -TimeoutSec 5 | Out-Null
            $weSetThePause = $true
            Write-Ok 'Dispatch paused for the duration of this backup.'
        }

        $deadline = (Get-Date).AddSeconds($QuiesceTimeoutSeconds)
        $drained = $false
        do {
            $dashboard = Invoke-RestMethod -Uri "$ApiUrl/api/dashboard" -TimeoutSec 5
            $active = $dashboard.metrics.activeTasks
            if ($active -eq 0) { $drained = $true; break }
            Write-Host "  waiting for $active active task(s) to finish..."
            Start-Sleep -Seconds 3
        } while ((Get-Date) -lt $deadline)

        if ($drained) { Write-Ok 'No active tasks - safe to snapshot.' }
        else { Write-WarnLine "Still $active active task(s) after ${QuiesceTimeoutSeconds}s - snapshotting anyway (database dump stays consistent; the worktree copy may catch mid-write files for that task)." }
    } catch {
        Write-WarnLine "Could not reach $ApiUrl ($_) - skipping quiescence. The dump itself is still consistent."
    }
} else {
    Write-Section 'Quiescing dispatch'
    Write-WarnLine 'Skipped (-SkipQuiesce).'
}

try {
    New-Item -ItemType Directory -Force -Path $snapshotDir | Out-Null

    # -----------------------------------------------------------------------------------------
    # Database dump - via Start-Process redirection (not PowerShell's `>` pipe), which would
    # re-encode the custom-format dump's binary bytes as text and silently corrupt it.
    # -----------------------------------------------------------------------------------------
    Write-Section 'PostgreSQL dump'
    $dumpFile = Join-Path $snapshotDir 'database.dump'
    $dumpArgs = @('compose', '-f', $composeFile, 'exec', '-T', 'postgres', 'pg_dump', '-U', $PostgresUser, '-Fc', '-d', $DatabaseName)
    $proc = Start-Process -FilePath 'docker' -ArgumentList $dumpArgs -RedirectStandardOutput $dumpFile -NoNewWindow -Wait -PassThru
    if ($proc.ExitCode -ne 0) { throw "pg_dump exited $($proc.ExitCode) - is 'docker compose up -d postgres' running? See ./scripts/start.ps1." }
    Write-Ok "Database '$DatabaseName' dumped to $dumpFile ($([math]::Round((Get-Item $dumpFile).Length / 1MB, 2)) MB)"

    # -----------------------------------------------------------------------------------------
    # Factory state directory - repository caches (bare clones, including every local branch,
    # pushed or not) and worktrees (uncommitted/untracked working-tree changes).
    # -----------------------------------------------------------------------------------------
    Write-Section 'Factory state directory'
    $stateDir = Join-Path $snapshotDir 'factory-data'
    if (Test-Path $RootDirectory) {
        robocopy $RootDirectory $stateDir /MIR /R:2 /W:1 /NFL /NDL /NP /NJH /NJS | Out-Null
        if ($LASTEXITCODE -ge 8) { throw "robocopy failed copying $RootDirectory (exit code $LASTEXITCODE)" }
        Write-Ok "Copied $RootDirectory to $stateDir"
    } else {
        Write-WarnLine "$RootDirectory does not exist - nothing to copy (no repositories cloned yet?)."
    }

    # -----------------------------------------------------------------------------------------
    # Manifest - what restore.ps1's own report is checked against.
    # -----------------------------------------------------------------------------------------
    $manifest = [ordered]@{
        createdAt     = (Get-Date).ToString('o')
        databaseName  = $DatabaseName
        rootDirectory = $RootDirectory
        factoryCommit = (git -C $repoRoot rev-parse HEAD 2>$null)
        quiesced      = -not $SkipQuiesce
    }
    $manifest | ConvertTo-Json | Set-Content -Path (Join-Path $snapshotDir 'manifest.json')
    Write-Ok "Manifest written to $(Join-Path $snapshotDir 'manifest.json')"
} finally {
    if ($weSetThePause) {
        try {
            Invoke-RestMethod -Uri "$ApiUrl/api/control/resume" -Method Post -TimeoutSec 5 | Out-Null
            Write-Ok 'Dispatch resumed (this script''s own pause only).'
        } catch {
            Write-WarnLine "Could not resume dispatch automatically ($_) - resume it manually: POST $ApiUrl/api/control/resume"
        }
    }
}

# ---------------------------------------------------------------------------------------------
# Retention
# ---------------------------------------------------------------------------------------------
Write-Section 'Retention'
if ($RetentionCount -le 0) {
    Write-WarnLine 'Pruning disabled (-RetentionCount 0).'
} elseif (Test-Path $Destination) {
    $snapshots = Get-ChildItem -Path $Destination -Directory | Where-Object { $_.Name -match '^\d{8}-\d{6}$' } | Sort-Object Name -Descending
    $toRemove = $snapshots | Select-Object -Skip $RetentionCount
    foreach ($old in $toRemove) {
        Remove-Item -Path $old.FullName -Recurse -Force
        Write-Ok "Pruned old snapshot $($old.Name)"
    }
    if (-not $toRemove) { Write-Ok "Nothing to prune (keeping $($snapshots.Count) of $RetentionCount)." }
}

Write-Host "`nBackup complete: $snapshotDir" -ForegroundColor Cyan
