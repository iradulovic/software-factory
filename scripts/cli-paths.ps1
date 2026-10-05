# The native Grok installer updates the user's PATH, but an already-running terminal may
# still have the old PATH. Add its install directory before launching factory services.
function Add-GrokBinToPath {
    $grokHomeDirectory = $env:GROK_HOME
    if ([string]::IsNullOrWhiteSpace($grokHomeDirectory)) {
        $grokHomeDirectory = Join-Path ([Environment]::GetFolderPath('UserProfile')) '.grok'
    }
    $grokBinDirectory = Join-Path $grokHomeDirectory 'bin'
    if (-not (Test-Path -LiteralPath $grokBinDirectory -PathType Container)) { return }

    foreach ($entry in ($env:PATH -split [System.IO.Path]::PathSeparator)) {
        if ([string]::Equals($entry.Trim('"').TrimEnd('\', '/'), $grokBinDirectory.TrimEnd('\', '/'), [System.StringComparison]::OrdinalIgnoreCase)) {
            return
        }
    }
    $env:PATH = "$grokBinDirectory$([System.IO.Path]::PathSeparator)$env:PATH"
}
