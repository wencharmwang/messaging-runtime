param(
    [ValidateSet('Debug','Release')][string]$Configuration = 'Release',
    [switch]$Test,
    [switch]$Publish
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'runtime-paths.ps1')
$dotnetPath = Get-MessagingDotnet
$solutionPath = Join-Path $MessagingProjectRoot 'MessagingRuntime.sln'
& $dotnetPath build $solutionPath -c $Configuration --verbosity minimal
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
if ($Test) {
    & $dotnetPath test $solutionPath -c $Configuration --no-build --no-restore --verbosity minimal
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
if ($Publish) {
    & $dotnetPath publish (Join-Path $MessagingProjectRoot 'src/host/MessagingRuntime.Host') -c $Configuration --no-restore -o (Join-Path $MessagingProjectRoot 'artifacts/app') --verbosity minimal
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
