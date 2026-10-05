# Pure planning helpers and bounded read-only probes for setup.ps1.
function Get-WingetInstallArgs([string]$PackageId) {
    if ($PackageId -notin @('Git.Git', 'GitHub.cli', 'Microsoft.DotNet.SDK.10', 'OpenJS.NodeJS')) {
        throw "Unapproved package ID: $PackageId"
    }
    return @('install', '--exact', '--id', $PackageId, '--source', 'winget', '--scope', 'user', '--accept-source-agreements', '--accept-package-agreements')
}

function Invoke-SetupProbe([string]$Executable, [string[]]$Arguments) {
    if (-not (Get-Command $Executable -ErrorAction SilentlyContinue)) { return [pscustomobject]@{ ok=$false; output='' } }
    $command = (Get-Command $Executable -ErrorAction SilentlyContinue).Source
    $quoted = @($Arguments | ForEach-Object { '"' + $_.Replace('"', '\"') + '"' }) -join ' '
    $info = New-Object System.Diagnostics.ProcessStartInfo
    $info.FileName = $command
    $info.Arguments = $quoted
    if ($command -match '\.(cmd|bat)$') {
        $info.FileName = $env:ComSpec
        $info.Arguments = '/d /s /c ""' + $command + '" ' + $quoted + '"'
    }
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    try {
        $process = [System.Diagnostics.Process]::Start($info)
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(10000)) {
            $process.Kill()
            return [pscustomobject]@{ ok=$false; output='' }
        }
        $output = $stdout.GetAwaiter().GetResult()
        $null = $stderr.GetAwaiter().GetResult()
        $code = $process.ExitCode
    } catch {
        $output = ''
        $code = 1
    } finally {
        if ($process) { $process.Dispose() }
    }
    return [pscustomobject]@{ ok=($code -eq 0); output=([string]$output).Trim() }
}

function Test-Prerequisite($Tool) {
    $probe = Invoke-SetupProbe $Tool.Exe $Tool.Args
    $ready = $probe.ok
    if ($ready -and $Tool.Name -eq '.NET SDK 10') { $ready = $probe.output -match '(?m)^10\.' }
    if ($ready -and $Tool.Name -eq 'Node.js 24+') {
        $match = [regex]::Match($probe.output, '^v?(\d+)\.')
        $ready = $match.Success -and [int]$match.Groups[1].Value -ge 24
    }
    if ($ready) { return [pscustomobject]@{ State='ready'; Detail="$($Tool.Exe) responds with a compatible version."; Action='' } }
    $state = if ($Tool.Package) { 'installable' } else { 'operator' }
    return [pscustomobject]@{ State=$state; Detail="$($Tool.Exe) is missing or incompatible."; Action=$Tool.Action }
}
function Get-InstallCandidates($Inventory) {
    return @($Inventory | Where-Object { $_.Result.State -ne 'ready' -and $_.Tool.Package } | ForEach-Object { $_.Tool })
}

function Get-WindowsReadiness {
    if ($env:OS -ne 'Windows_NT') { return [pscustomobject]@{ State='operator'; Detail='Windows is required for this wizard.'; Action='Use a supported Windows 10/11 machine.' } }
    $build = [Environment]::OSVersion.Version.Build
    if ($build -lt 19045) { return [pscustomobject]@{ State='operator'; Detail="Windows build $build is below the supported baseline."; Action='Update Windows before installing Docker Desktop.' } }
    return [pscustomobject]@{ State='ready'; Detail="Windows build $build."; Action='' }
}
function Get-ShellReadiness {
    if ($PSVersionTable.PSVersion.Major -lt 5) { return [pscustomobject]@{ State='operator'; Detail='PowerShell 5.1 or newer is required.'; Action='Update Windows PowerShell or install PowerShell 7.' } }
    return [pscustomobject]@{ State='ready'; Detail="PowerShell $($PSVersionTable.PSVersion)."; Action='' }
}
function Get-WslReadiness {
    if (Get-Command 'wsl.exe' -ErrorAction SilentlyContinue) {
        # WSL is a Windows app alias: spawning it through ProcessStartInfo can return E_ACCESSDENIED
        # under some hosts even when invoking the alias directly from PowerShell succeeds.
        $oldPreference = $ErrorActionPreference
        $ErrorActionPreference = 'Continue'
        try { $wslOutput = (& wsl.exe --version 2>$null | Out-String) -replace "`0", ''; $wslExit = $LASTEXITCODE }
        catch { $wslExit = 1 }
        finally { $ErrorActionPreference = $oldPreference }
        if ($wslExit -eq 0) {
            $versionMatch = [regex]::Match($wslOutput, '(?i)WSL version:\s*(\d+\.\d+\.\d+)')
            if ($versionMatch.Success -and [version]$versionMatch.Groups[1].Value -lt [version]'2.1.5') {
                return [pscustomobject]@{ State='needs-elevation/reboot'; Detail="WSL $($versionMatch.Groups[1].Value) is below 2.1.5."; Action='Run wsl --update in an elevated terminal, then rerun setup.' }
            }
            return [pscustomobject]@{ State='ready'; Detail='WSL responds; verify WSL 2 is enabled if using Docker Desktop.'; Action='' }
        }
    }
    return [pscustomobject]@{ State='needs-elevation/reboot'; Detail='WSL is unavailable or needs an update; an existing compatible Docker daemon may still work.'; Action='For Docker Desktop WSL backend, run wsl --install from an elevated terminal, reboot if requested, and rerun setup.' }
}
function Get-ContainerReadiness([string]$RepoRoot) {
    $client = Invoke-SetupProbe 'docker' @('version', '--format', '{{.Client.Version}}')
    $server = Invoke-SetupProbe 'docker' @('version', '--format', '{{.Server.Version}}')
    $compose = Invoke-SetupProbe 'docker' @('compose', 'version', '--short')
    if (-not ($client.ok -and $server.ok -and $compose.ok)) {
        return [pscustomobject]@{ State='operator'; Detail='Docker client, server, and Compose are all required; CLI presence alone is insufficient.'; Action='Start an existing compatible daemon or install and start Docker Desktop with WSL 2, then rerun.' }
    }
    $file = Join-Path $RepoRoot 'docker-compose.yml'
    $config = Invoke-SetupProbe 'docker' @('compose', '-f', $file, 'config', '--quiet')
    if (-not $config.ok) { return [pscustomobject]@{ State='operator'; Detail='Project Compose configuration failed validation.'; Action='Run docker compose -f docker-compose.yml config and fix the reported error.' } }
    return [pscustomobject]@{ State='ready'; Detail='Docker client, server, and project Compose configuration respond.'; Action='' }
}

function Get-AgentProfiles([string]$RepoRoot) {
    $path = Join-Path $RepoRoot 'src/Factory.Orchestrator/appsettings.json'
    $settings = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
    $profiles = @($settings.Agents.Profiles)
    # ASP.NET configuration environment override: empty executable disables a profile.
    for ($i = 0; $i -lt $profiles.Count; $i++) {
        $key = "Agents__Profiles__${i}__Executable"
        $override = [Environment]::GetEnvironmentVariable($key)
        if ($null -ne $override) { $profiles[$i].Executable = $override }
    }
    return @($profiles | Where-Object { -not [string]::IsNullOrWhiteSpace($_.Executable) })
}
function Test-AgentProfile($Profile) {
    $args = if ($Profile.VersionArguments) { @($Profile.VersionArguments) } else { @('--version') }
    $probe = Invoke-SetupProbe $Profile.Executable $args
    if ($probe.ok) {
        $authArgs = switch ($Profile.Name) {
            'Codex' { @('login', 'status') }
            'Claude' { @('auth', 'status') }
            'Pi' { @('auth', 'check', '--model', 'moonshotai/kimi-k2.6', '--json') }
            'Grok' { @('models') }
            default { @() }
        }
        if ($authArgs.Count -eq 0) { return [pscustomobject]@{ State='ready'; Detail="$($Profile.Executable) responds; verify sign-in in its own CLI session."; Action='' } }
        $auth = Invoke-SetupProbe $Profile.Executable $authArgs
        if ($auth.ok) { return [pscustomobject]@{ State='ready'; Detail="$($Profile.Executable) responds and its auth check passed."; Action='' } }
        return [pscustomobject]@{ State='operator'; Detail="$($Profile.Executable) responds but its auth check did not pass."; Action="Sign in with the $($Profile.Name) CLI in your own non-admin terminal, then rerun." }
    }
    $hint = switch ($Profile.Name) {
        'Codex' { 'Install the Codex CLI, then run codex login in your own terminal.' }
        'Claude' { 'Install Claude Code from its official guide, then run claude login in your own terminal.' }
        'Pi' { 'Install Pi using its official guide, then run pi auth check --model moonshotai/kimi-k2.6 --json.' }
        'Grok' { 'Install Grok Build from https://x.ai/cli, then run grok login in your own terminal.' }
        default { "Install the configured $($Profile.Executable) executable and sign in as the service user." }
    }
    return [pscustomobject]@{ State='operator'; Detail="$($Profile.Executable) is missing or does not respond."; Action=$hint }
}

function Get-SetupDoctor([string]$RepoRoot) {
    $results = @()
    $gh = Invoke-SetupProbe 'gh' @('auth', 'status', '-h', 'github.com')
    $results += [pscustomobject]@{ name='GitHub authentication'; state=$(if ($gh.ok) {'ready'} else {'operator'}); detail=$(if ($gh.ok) {'Authenticated CLI session.'} else {'Run gh auth login -h github.com in your own terminal.'}) }
    $docker = Get-ContainerReadiness $RepoRoot
    $results += [pscustomobject]@{ name='Container runtime'; state=$docker.State; detail=$docker.Detail }
    $api = $false
    try { $api = (Invoke-WebRequest -Uri 'http://localhost:5080/health' -UseBasicParsing -TimeoutSec 5).StatusCode -eq 200 } catch {}
    $results += [pscustomobject]@{ name='API and migrations'; state=$(if ($api) {'ready'} else {'operator'}); detail=$(if ($api) {'Health endpoint passed; API runs migrations on startup.'} else {'Run scripts/start.ps1 and inspect logs/api.err.log.'}) }
    $workers = $false
    if ($api) {
        try { $response = Invoke-RestMethod -Uri 'http://localhost:5080/api/workers' -TimeoutSec 5; $workers = @($response | Where-Object { -not $_.isStale }).Count -gt 0 } catch {}
    }
    $results += [pscustomobject]@{ name='Worker'; state=$(if ($workers) {'ready'} else {'operator'}); detail=$(if ($workers) {'Fresh worker heartbeat.'} else {'No fresh worker heartbeat. Inspect logs/orchestrator.err.log.'}) }
    $dashboard = $false
    try { $dashboard = (Invoke-WebRequest -Uri 'http://localhost:3000/' -UseBasicParsing -TimeoutSec 5).StatusCode -eq 200 } catch {}
    $results += [pscustomobject]@{ name='Dashboard'; state=$(if ($dashboard) {'ready'} else {'operator'}); detail=$(if ($dashboard) {'Dashboard HTTP 200.'} else {'Run scripts/start.ps1 and inspect logs/dashboard.err.log.'}) }
    $composeFile = Join-Path $RepoRoot 'docker-compose.yml'
    $project = Split-Path -Leaf $RepoRoot
    $pg = Invoke-SetupProbe 'docker' @('compose', '-f', $composeFile, '-p', $project, 'exec', '-T', 'postgres', 'pg_isready', '-U', 'factory', '-d', 'software_factory')
    $results += [pscustomobject]@{ name='PostgreSQL'; state=$(if ($pg.ok) {'ready'} else {'operator'}); detail=$(if ($pg.ok) {'Compose PostgreSQL accepts connections.'} else {'Start PostgreSQL using scripts/start.ps1; inspect docker compose logs postgres.'}) }
    $migrations = Invoke-SetupProbe 'docker' @('compose', '-f', $composeFile, '-p', $project, 'exec', '-T', 'postgres', 'psql', '-U', 'factory', '-d', 'software_factory', '-tAc', 'select count(*) from factory.schema_migration')
    $migrated = $migrations.ok -and $migrations.output -match '^\d+$' -and [int]$migrations.output -gt 0
    $results += [pscustomobject]@{ name='Migrations'; state=$(if ($migrated) {'ready'} else {'operator'}); detail=$(if ($migrated) {'Migration ledger contains applied migrations.'} else {'Start the API and inspect logs/api.err.log for migration errors.'}) }
    return $results
}

function Test-ServicesWorktreeSafe([string]$RepoRoot) {
    $path = Join-Path $RepoRoot '.worktrees/services'
    if (-not (Test-Path -LiteralPath $path)) { return }
    $probe = Invoke-SetupProbe 'git' @('-C', $path, 'status', '--porcelain', '--untracked-files=normal')
    if (-not $probe.ok -or $probe.output) {
        throw 'The existing .worktrees/services directory has changes or is not a Git worktree. Resolve or back up those files before start.ps1 resets it.'
    }
}

function Register-FactoryRepository([string]$Repository, [string]$DefaultBranch) {
    $parts = $Repository.Split('/')
    $existing = Invoke-RestMethod -Uri 'http://localhost:5080/api/repositories' -TimeoutSec 5
    if (@($existing | Where-Object { $_.owner -eq $parts[0] -and $_.name -eq $parts[1] }).Count -gt 0) {
        Write-Host "Already registered: $Repository"
        return
    }
    $body = @{ owner=$parts[0]; name=$parts[1]; defaultBranch=$DefaultBranch; cloneUrl="https://github.com/$Repository.git" } | ConvertTo-Json
    Invoke-RestMethod -Uri 'http://localhost:5080/api/repositories' -Method Post -ContentType 'application/json' -Body $body -TimeoutSec 10 | Out-Null
    Write-Host "Registered: $Repository"
}
