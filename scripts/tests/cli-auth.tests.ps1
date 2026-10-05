$ErrorActionPreference = 'Stop'
. (Join-Path (Split-Path -Parent $PSScriptRoot) 'cli-auth.ps1')
function Assert($Condition, $Message) { if (-not $Condition) { throw $Message } }

$failureSignatures = @('', ' ', 'You are not authenticated.')
Assert (-not (Test-AuthenticationProbe -Succeeded $true -StandardOutput 'You are not authenticated.' -FailureSignatures $failureSignatures)) 'Exit 0 must not hide signed-out stdout.'
Assert (-not (Test-AuthenticationProbe -Succeeded $true -StandardError 'YOU ARE NOT AUTHENTICATED.' -FailureSignatures $failureSignatures)) 'Exit 0 must not hide signed-out stderr.'
Assert (Test-AuthenticationProbe -Succeeded $true -StandardOutput 'You are logged in with grok.com.' -FailureSignatures $failureSignatures) 'Existing login should pass.'
Assert (Test-AuthenticationProbe -Succeeded $true -StandardOutput 'Using XAI_API_KEY.' -FailureSignatures $failureSignatures) 'API-key authentication should pass.'
Assert (-not (Test-AuthenticationProbe -Succeeded $false -StandardOutput 'You are logged in with grok.com.' -FailureSignatures $failureSignatures)) 'Nonzero probes must fail regardless of output.'
Assert (Test-AuthenticationProbe -Succeeded $true) 'Profiles without signatures must retain their exit-code behavior.'

# Exercise start.ps1's real checker without running its service-startup side effects.
$startScriptPath = Join-Path (Split-Path -Parent $PSScriptRoot) 'start.ps1'
$startScript = [System.Management.Automation.Language.Parser]::ParseFile($startScriptPath, [ref]$null, [ref]$null)
$checker = $startScript.Find({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Test-Cli' }, $false)
Invoke-Expression $checker.Extent.Text
function Write-Ok($Text) { }
function Write-WarnLine($Text) { $script:lastWarning = $Text }
function Write-FailLine($Text) { throw $Text }
function Mock-Grok { $global:LASTEXITCODE = 0; 'You are not authenticated.' }
Assert (-not (Test-Cli -Name 'Mock-Grok' -CheckArgs @('models') -FailureSignatures $failureSignatures -Hint 'Run grok login.')) 'Startup must not report signed-out Grok as responding.'
Assert ($script:lastWarning -match 'authentication failure' -and $script:lastWarning -notmatch 'You are not authenticated') 'Startup must report a sanitized auth failure.'
Write-Host 'CLI authentication tests passed'
