param(
    [ValidateSet('Debug','Release')][string]$Configuration = 'Release',
    [switch]$Test,
    [switch]$Publish,
    # 自包含发布：把 .NET 运行时一起打进 artifacts/app，客户机**不需要装 .NET**。
    # 交付给客户时用这个；平时本机开发用默认（框架依赖，输出小、构建快）。
    [switch]$SelfContained,
    [ValidateSet('win-x64','win-arm64')][string]$RuntimeIdentifier = 'win-x64'
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
    $publishArgs = @(
        'publish', (Join-Path $MessagingProjectRoot 'src/host/MessagingRuntime.Host'),
        '-c', $Configuration, '-o', (Join-Path $MessagingProjectRoot 'artifacts/app'),
        '--verbosity', 'minimal'
    )
    if ($SelfContained) {
        # 自包含必须先按 RID 还原（runtime pack 来自 NuGet）—— 这时**不能**加 --no-restore。
        $publishArgs += @('-r', $RuntimeIdentifier, '--self-contained', 'true')
    } else {
        $publishArgs += '--no-restore'
    }
    & $dotnetPath @publishArgs
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
