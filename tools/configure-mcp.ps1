param([switch]$Force)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'runtime-paths.ps1')
$clientFile = Join-Path $MessagingProjectRoot '.runtime-data/mcp-client.local.json'
if ((Test-Path -LiteralPath $clientFile) -and -not $Force) { throw 'Client config already exists. Use -Force to regenerate it after moving the project.' }
$config = @{ mcpServers = @{ 'messaging-runtime' = @{
    command = Get-MessagingDotnet
    args = @(
        (Join-Path $MessagingProjectRoot 'artifacts/app/messaging-runtime.dll'),
        '--stdio', '--config',
        (Join-Path $MessagingProjectRoot 'config/messaging-runtime.example.json')
    )
} } }
[IO.Directory]::CreateDirectory((Split-Path -Parent $clientFile)) | Out-Null
$config | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $clientFile -Encoding utf8
Write-Output $clientFile
