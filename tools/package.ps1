param(
  [switch]$SkipBuild,
  # 交付给客户时带上这个：把 .NET 运行时一起打进去，客户机不用装 .NET。
  # 用它就不需要客户机有任何前置（除了微信本身）。
  [switch]$SelfContained
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'runtime-paths.ps1')
if (-not $SkipBuild) {
    & (Join-Path $PSScriptRoot 'build.ps1') -Publish -SelfContained:$SelfContained
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
$appPath = Join-Path $MessagingProjectRoot 'artifacts/app'
if (-not (Test-Path -LiteralPath (Join-Path $appPath 'messaging-runtime.dll'))) { throw 'Published runtime is missing.' }
$packagePath = Join-Path $MessagingProjectRoot ('artifacts/packages/messaging-runtime-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
if (Test-Path -LiteralPath $packagePath) { throw 'Package already exists; retry with a new timestamp.' }
foreach ($directory in @('artifacts','tools','config','runtime')) { [IO.Directory]::CreateDirectory((Join-Path $packagePath $directory)) | Out-Null }
Copy-Item -LiteralPath $appPath -Destination (Join-Path $packagePath 'artifacts/app') -Recurse
foreach ($tool in @('runtime-paths.ps1','start-messaging-runtime.ps1','configure-mcp.ps1','ocr_server.py','messaging_agent_example.py')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $tool) -Destination (Join-Path $packagePath 'tools')
}
Copy-Item -LiteralPath (Join-Path $MessagingProjectRoot 'config/messaging-runtime.example.json') -Destination (Join-Path $packagePath 'config')
Copy-Item -LiteralPath (Join-Path $MessagingProjectRoot 'runtime/python') -Destination (Join-Path $packagePath 'runtime/python') -Recurse
Copy-Item -LiteralPath (Join-Path $MessagingProjectRoot 'README.md') -Destination $packagePath
Copy-Item -LiteralPath (Join-Path $MessagingProjectRoot 'docs') -Destination (Join-Path $packagePath 'docs') -Recurse
Write-Output "Package created: $packagePath"
