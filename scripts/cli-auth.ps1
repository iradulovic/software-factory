# Some authentication probes (including grok models) exit 0 while reporting no session.
# Inspect configured failure messages without printing or persisting provider output.
function Test-AuthenticationProbe {
    param([bool]$Succeeded, [string]$StandardOutput, [string]$StandardError, [string[]]$FailureSignatures)
    if (-not $Succeeded) { return $false }
    foreach ($signature in $FailureSignatures) {
        if ([string]::IsNullOrWhiteSpace($signature)) { continue }
        if ($StandardOutput.IndexOf($signature, [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
            $StandardError.IndexOf($signature, [System.StringComparison]::OrdinalIgnoreCase) -ge 0) { return $false }
    }
    return $true
}
