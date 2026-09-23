<#
.SYNOPSIS
  Restore a backup.ps1 snapshot into a separate, disposable location for verification (SF-616).

.DESCRIPTION
  Restores a snapshot produced by backup.ps1 into a brand-new database and a brand-new factory-data directory -
  never the live database or the live RootDirectory - so a restore can always be verified safely, without any
  risk to (or interference from) whatever the live factory is currently doing. This script never starts, pauses,
  or resumes any service, and it never touches factory.dispatch_pause: recovery must not start dispatch
  automatically against an unverified restore, and since this restores into a location nothing live reads from,
  there's nothing to start in the first place. Promoting a verified restore to be the live system (stopping the
  live services, swapping in the restored database/RootDirectory) is a separate, deliberate operator action -
  see README's backup/restore section - not something this script does for you.

  After restoring, it prints verification evidence for each of the three things a backup exists to protect that
  GitHub alone would not:
    - task history:      row counts and the most recent tasks from factory.task in the restored database
    - an unpushed commit: local branches, per repository cache, that are not reachable from any origin/* ref
    - uncommitted work:   `git status --porcelain` for every restored worktree

  Git worktrees record an absolute path back to their repository cache's internal administrative directory, so a
  worktree copied to a new location can't be used by Git as-is; this script repairs that link (via Git's own
  `worktree repair`) for each restored worktree before checking it.

.PARAMETER BackupPath
  Path to a specific snapshot directory produced by backup.ps1 (e.g. ./backups/20260923-141500).

.PARAMETER TargetDirectory
  Where the restored factory-data/ tree is written. Defaults to a fresh temp directory. Refused if it resolves to
  the live RootDirectory (pass -Force to override, though there is normally no good reason to).

.PARAMETER TargetDatabase
  Name of a brand-new database to restore into (created by this script). Defaults to a timestamped
  software_factory_restore_* name. Refused if it equals -LiveDatabaseName (pass -Force to override).

.PARAMETER LiveDatabaseName
  The live database name this script guards -TargetDatabase against colliding with. Defaults to software_factory.

.PARAMETER PostgresUser
  Postgres role used for createdb/pg_restore/psql, run inside the Compose postgres container. Defaults to factory.

.PARAMETER Force
  Allow -TargetDirectory/-TargetDatabase to equal the live location/name. Not recommended.

.EXAMPLE
  ./scripts/restore.ps1 -BackupPath ./backups/20260923-141500
.EXAMPLE
  ./scripts/restore.ps1 -BackupPath ./backups/20260923-141500 -TargetDirectory D:\restore-check -TargetDatabase software_factory_check
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$BackupPath,
    [string]$TargetDirectory = (Join-Path ([System.IO.Path]::GetTempPath()) "factory-restore-$(Get-Date -Format 'yyyyMMdd-HHmmss')"),
    [string]$TargetDatabase = "software_factory_restore_$(Get-Date -Format 'yyyyMMdd_HHmmss')",
    [string]$LiveDatabaseName = 'software_factory',
    [string]$PostgresUser = 'factory',
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$composeFile = Join-Path $repoRoot 'docker-compose.yml'

function Write-Section($text) { Write-Host "`n== $text ==" -ForegroundColor Cyan }
function Write-Ok($text) { Write-Host "  [OK]   $text" -ForegroundColor Green }
function Write-WarnLine($text) { Write-Host "  [WARN] $text" -ForegroundColor Yellow }
function Write-FailLine($text) { Write-Host "  [FAIL] $text" -ForegroundColor Red }

$dumpFile = Join-Path $BackupPath 'database.dump'
$manifestFile = Join-Path $BackupPath 'manifest.json'
$stateSourceDir = Join-Path $BackupPath 'factory-data'
if (-not (Test-Path $dumpFile)) { throw "$dumpFile not found - is -BackupPath a snapshot directory produced by backup.ps1?" }
$manifest = if (Test-Path $manifestFile) { Get-Content $manifestFile -Raw | ConvertFrom-Json } else { $null }
if ($manifest) { Write-Host "Restoring snapshot created $($manifest.createdAt) (quiesced: $($manifest.quiesced))" }

if (-not $Force -and $TargetDatabase -eq $LiveDatabaseName) {
    throw "-TargetDatabase '$TargetDatabase' equals the live database name. Refusing without -Force - restore into a separate, disposable database."
}
$liveRootDirectory = Join-Path $repoRoot 'factory-data'
$normalizedTarget = [System.IO.Path]::GetFullPath($TargetDirectory).TrimEnd('\', '/')
$normalizedLiveRoot = [System.IO.Path]::GetFullPath($liveRootDirectory).TrimEnd('\', '/')
if (-not $Force -and $normalizedTarget -ieq $normalizedLiveRoot) {
    throw "-TargetDirectory resolves to the live RootDirectory ($liveRootDirectory). Refusing without -Force - restore into a separate location."
}

# ---------------------------------------------------------------------------------------------
# Database - create a fresh database and restore into it. Never touches the live database.
# ---------------------------------------------------------------------------------------------
Write-Section "Database (creating '$TargetDatabase')"
$createArgs = @('compose', '-f', $composeFile, 'exec', '-T', 'postgres', 'createdb', '-U', $PostgresUser, $TargetDatabase)
$proc = Start-Process -FilePath 'docker' -ArgumentList $createArgs -NoNewWindow -Wait -PassThru
if ($proc.ExitCode -ne 0) { throw "createdb exited $($proc.ExitCode) - is 'docker compose up -d postgres' running? See ./scripts/start.ps1." }
Write-Ok "Database '$TargetDatabase' created"

Write-Section 'Database restore'
$restoreArgs = @('compose', '-f', $composeFile, 'exec', '-T', 'postgres', 'pg_restore', '-U', $PostgresUser, '-d', $TargetDatabase, '--no-owner')
$proc = Start-Process -FilePath 'docker' -ArgumentList $restoreArgs -RedirectStandardInput $dumpFile -NoNewWindow -Wait -PassThru
if ($proc.ExitCode -ne 0) { throw "pg_restore exited $($proc.ExitCode)" }
Write-Ok "Restored $dumpFile into '$TargetDatabase'"

# ---------------------------------------------------------------------------------------------
# Factory state directory
# ---------------------------------------------------------------------------------------------
Write-Section 'Factory state directory'
if (Test-Path $stateSourceDir) {
    New-Item -ItemType Directory -Force -Path $TargetDirectory | Out-Null
    robocopy $stateSourceDir $TargetDirectory /MIR /R:2 /W:1 /NFL /NDL /NP /NJH /NJS | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "robocopy failed copying $stateSourceDir (exit code $LASTEXITCODE)" }
    Write-Ok "Copied $stateSourceDir to $TargetDirectory"
} else {
    Write-WarnLine "$stateSourceDir not present in this snapshot - nothing to restore (backup was taken before any repository existed)."
}

# ---------------------------------------------------------------------------------------------
# Verification evidence
# ---------------------------------------------------------------------------------------------
Write-Section 'Verification: task history'
$psqlArgs = @('compose', '-f', $composeFile, 'exec', '-T', 'postgres', 'psql', '-U', $PostgresUser, '-d', $TargetDatabase, '-c',
    "select status, count(*) from factory.task group by status order by status;")
& docker @psqlArgs
$psqlArgs = @('compose', '-f', $composeFile, 'exec', '-T', 'postgres', 'psql', '-U', $PostgresUser, '-d', $TargetDatabase, '-c',
    "select id, repository_id, github_issue_id, status, title from factory.task order by created_at desc limit 5;")
& docker @psqlArgs

if (Test-Path $TargetDirectory) {
    $reposRoot = Join-Path $TargetDirectory 'repositories'
    $worktreesRoot = Join-Path $TargetDirectory 'worktrees'

    Write-Section 'Verification: unpushed commits (local branches not reachable from any origin/* ref)'
    if (Test-Path $reposRoot) {
        Get-ChildItem -Path $reposRoot -Recurse -Directory -Filter '*.git' | ForEach-Object {
            $bare = $_.FullName
            $unpushed = git --git-dir="$bare" log --branches --not --remotes --oneline 2>$null
            if ($unpushed) {
                Write-WarnLine "$bare has commit(s) not on any remote-tracking ref:"
                $unpushed | ForEach-Object { Write-Host "    $_" }
            } else {
                Write-Ok "$bare - every local branch is reachable from some origin/* ref"
            }
        }
    } else {
        Write-WarnLine "$reposRoot not present in this restore."
    }

    Write-Section 'Verification: uncommitted work'
    if (Test-Path $worktreesRoot) {
        # A relocated worktree's .git file still points at its old, now-copied-elsewhere repository cache's
        # internal admin directory; `git worktree repair` fixes that link (both directions) before use.
        Get-ChildItem -Path $worktreesRoot -Recurse -Filter '.git' -File -Force | ForEach-Object {
            $worktreePath = $_.Directory.FullName
            # `worktree repair` writes an informational note to stderr while it fixes the link; with this
            # script's $ErrorActionPreference = 'Stop', an unredirected native stderr line would otherwise
            # abort the whole restore, so it's swallowed here rather than treated as a real failure.
            try { git -C $worktreePath worktree repair 2>$null | Out-Null } catch { }
            $status = $null
            try { $status = git -C $worktreePath status --porcelain 2>$null } catch { }
            if ($LASTEXITCODE -ne 0) {
                Write-WarnLine "$worktreePath - could not run 'git status' after repair (see above)."
            } elseif ($status) {
                Write-WarnLine "$worktreePath has uncommitted/untracked changes:"
                $status | ForEach-Object { Write-Host "    $_" }
            } else {
                Write-Ok "$worktreePath - clean (no uncommitted work)"
            }
        }
    } else {
        Write-WarnLine "$worktreesRoot not present in this restore."
    }
}

Write-Host "`nRestore complete." -ForegroundColor Cyan
Write-Host "  Database:  $TargetDatabase (separate from the live '$LiveDatabaseName')" -ForegroundColor Cyan
Write-Host "  Directory: $TargetDirectory (separate from the live factory-data/)" -ForegroundColor Cyan
Write-Host "  No service was started and dispatch was not touched - this restore is inert until you act on it." -ForegroundColor Cyan
Write-Host "  Drop the verification database when done: docker compose -f `"$composeFile`" exec postgres dropdb -U $PostgresUser $TargetDatabase" -ForegroundColor Cyan
