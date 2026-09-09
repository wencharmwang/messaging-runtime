param([string[]]$RuntimeArgs = @('--stdio'))
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'runtime-paths.ps1')
$runtimeDll = Join-Path $MessagingProjectRoot 'artifacts/app/messaging-runtime.dll'
if (-not (Test-Path -LiteralPath $runtimeDll)) { throw 'Run tools/build.ps1 -Publish first.' }
$runtimeDotnet = Get-MessagingDotnet
Push-Location -LiteralPath $MessagingProjectRoot
try { & $runtimeDotnet $runtimeDll @RuntimeArgs; $runtimeExitCode = $LASTEXITCODE }
finally { Pop-Location }
exit $runtimeExitCode
