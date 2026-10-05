$ErrorActionPreference = 'Stop'
. (Join-Path (Split-Path -Parent $PSScriptRoot) 'setup.lib.ps1')
function Assert($Condition, $Message) { if (-not $Condition) { throw $Message } }

foreach ($id in @('Git.Git', 'GitHub.cli', 'Microsoft.DotNet.SDK.10', 'OpenJS.NodeJS')) {
    $args = Get-WingetInstallArgs $id
    Assert ($args[0] -eq 'install' -and $args[3] -eq $id -and $args[5] -eq 'winget') "Incorrect install arguments for $id"
}
$rejected = $false
try { Get-WingetInstallArgs 'Untrusted.Package' | Out-Null } catch { $rejected = $true }
Assert $rejected 'Unknown package ID was accepted.'

$original = (Get-Command Invoke-SetupProbe).ScriptBlock
try {
    $script:grokProbeAuthenticated = $true
    $script:grokProbeErrorStream = $false
    function Invoke-SetupProbe([string]$Executable, [string[]]$Arguments) {
        switch ($Executable) {
            'dotnet' { return [pscustomobject]@{ ok=$true; output='9.0.1 [path]' } }
            'node' { return [pscustomobject]@{ ok=$true; output='v24.1.0' } }
            'docker' {
                if ($Arguments[0] -eq 'version' -and $Arguments[2] -eq '{{.Server.Version}}') { return [pscustomobject]@{ ok=$false; output='' } }
                return [pscustomobject]@{ ok=$true; output='1' }
            }
            default { return [pscustomobject]@{ ok=$false; output='' } }
        }
    }
    $sdk = Test-Prerequisite @{ Name='.NET SDK 10'; Exe='dotnet'; Args=@('--list-sdks'); Package='Microsoft.DotNet.SDK.10'; Action='Install.' }
    Assert ($sdk.State -eq 'installable') 'Runtime/old SDK must not pass as SDK 10.'
    $node = Test-Prerequisite @{ Name='Node.js 24+'; Exe='node'; Args=@('--version'); Package='OpenJS.NodeJS'; Action='Install.' }
    Assert ($node.State -eq 'ready') 'Node 24 should pass.'
    $plan = @(Get-InstallCandidates @(
        [pscustomobject]@{ Tool=@{ Package='Microsoft.DotNet.SDK.10' }; Result=$sdk },
        [pscustomobject]@{ Tool=@{ Package='OpenJS.NodeJS' }; Result=$node }
    ))
    Assert ($plan.Count -eq 1 -and $plan[0].Package -eq 'Microsoft.DotNet.SDK.10') 'Rerun plan must skip already-ready tools.'
    $container = Get-ContainerReadiness (Split-Path -Parent (Split-Path -Parent $PSScriptRoot))
    Assert ($container.State -ne 'ready') 'Client and Compose without a server must fail.'
} finally {
    Set-Item Function:Invoke-SetupProbe $original
}

$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$profiles = @(Get-AgentProfiles $root)
Assert ($profiles.Count -eq 4) 'Configured profiles were not read.'

$original = (Get-Command Invoke-SetupProbe).ScriptBlock
try {
    function Invoke-SetupProbe([string]$Executable, [string[]]$Arguments) {
        Assert ($Executable -eq 'grok') 'Grok readiness must use the configured executable.'
        Assert (($Arguments -join ' ') -in @('--version', 'models')) 'Grok readiness must not invoke a model or login flow.'
        if ($Arguments[0] -eq '--version') { return [pscustomobject]@{ ok=$true; output='grok 1.0.46'; errorOutput='' } }
        $message = if ($script:grokProbeAuthenticated) { 'You are logged in with grok.com.' } else { 'You are not authenticated.' }
        return [pscustomobject]@{ ok=$true; output=$(if ($script:grokProbeErrorStream) { '' } else { $message }); errorOutput=$(if ($script:grokProbeErrorStream) { $message } else { '' }) }
    }
    $grok = Test-AgentProfile ($profiles | Where-Object { $_.Name -eq 'Grok' })
    Assert ($grok.State -eq 'ready' -and $grok.Detail -match 'auth check passed') 'Grok must pass version and authentication probes.'
    $script:grokProbeAuthenticated = $false
    foreach ($errorStream in @($false, $true)) {
        $script:grokProbeErrorStream = $errorStream
        $grok = Test-AgentProfile ($profiles | Where-Object { $_.Name -eq 'Grok' })
        Assert ($grok.State -eq 'operator') 'Signed-out Grok must fail readiness even with exit 0 on either stream.'
        Assert ($grok.Detail -notmatch 'You are not authenticated') 'Provider auth output must not be included in setup results.'
    }
} finally {
    Set-Item Function:Invoke-SetupProbe $original
}
Write-Host 'setup tests passed'
