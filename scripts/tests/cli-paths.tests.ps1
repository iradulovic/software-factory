$ErrorActionPreference = 'Stop'
. (Join-Path (Split-Path -Parent $PSScriptRoot) 'cli-paths.ps1')
function Assert($Condition, $Message) { if (-not $Condition) { throw $Message } }

$originalPath = $env:PATH
$originalGrokHome = $env:GROK_HOME
$testDirectory = Join-Path ([System.IO.Path]::GetTempPath()) ('factory-grok-path-' + [guid]::NewGuid())
$testBin = Join-Path $testDirectory 'bin'
try {
    $env:GROK_HOME = $testDirectory
    $env:PATH = $originalPath
    Add-GrokBinToPath
    Assert ($env:PATH -eq $originalPath) 'A nonexistent Grok install must not change PATH.'

    New-Item -ItemType Directory -Path $testBin -Force | Out-Null
    Add-GrokBinToPath
    $expectedPath = "$testBin$([System.IO.Path]::PathSeparator)$originalPath"
    Assert ($env:PATH -eq $expectedPath) 'GROK_HOME/bin must be inherited by child processes.'
    Add-GrokBinToPath
    Assert ($env:PATH -eq $expectedPath) 'Repeated initialization must not duplicate Grok on PATH.'

    $quotedPath = '"' + $testBin.ToUpperInvariant() + '\"' + [System.IO.Path]::PathSeparator + $originalPath
    $env:PATH = $quotedPath
    Add-GrokBinToPath
    Assert ($env:PATH -eq $quotedPath) 'An existing quoted/case-varied Grok PATH entry must not be duplicated.'
} finally {
    $env:PATH = $originalPath
    $env:GROK_HOME = $originalGrokHome
    $resolvedTestDirectory = [System.IO.Path]::GetFullPath($testDirectory)
    $temporaryRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
    if ($resolvedTestDirectory.StartsWith($temporaryRoot, [System.StringComparison]::OrdinalIgnoreCase) -and
        (Split-Path -Leaf $resolvedTestDirectory) -like 'factory-grok-path-*') {
        if (Test-Path -LiteralPath $resolvedTestDirectory) { Remove-Item -LiteralPath $resolvedTestDirectory -Recurse -Force }
    }
}
Write-Host 'CLI path tests passed'
