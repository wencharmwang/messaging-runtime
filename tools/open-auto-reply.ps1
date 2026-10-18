$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path $PSScriptRoot -Parent
$taskConfig = Get-Content -LiteralPath (Join-Path $taskRoot '.runtime-data\auto-reply\config.json') -Raw | ConvertFrom-Json
$taskToken = (Get-Content -LiteralPath $taskConfig.control_token_file -Raw).Trim()
# A URL fragment is never sent in HTTP requests. The dashboard clears it after reading.
Start-Process -FilePath "http://127.0.0.1:$($taskConfig.control_port)/#$taskToken" -WindowStyle Hidden
