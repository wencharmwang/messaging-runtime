param(
    [string]$HarnessRoot = 'C:\Users\Wencharm\Documents\projects\deepseek-harness',
    [string]$NodeExecutable,
    [switch]$PrepareOnly
)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path $PSScriptRoot -Parent
$taskData = Join-Path $taskRoot '.runtime-data\auto-reply'
$taskPython = Join-Path $taskRoot 'runtime\python\python.exe'
$taskPythonw = Join-Path $taskRoot 'runtime\python\pythonw.exe'
if (-not $NodeExecutable) { $NodeExecutable = (Get-Command node).Source }
foreach ($taskRequired in @($taskPython,$taskPythonw,$NodeExecutable,(Join-Path $HarnessRoot 'apps\cli\src\bin.ts'))) {
    if (-not (Test-Path -LiteralPath $taskRequired)) { throw "Missing runtime dependency: $taskRequired" }
}
New-Item -ItemType Directory -Path $taskData -Force | Out-Null
foreach ($taskName in @('worker.token','control.token')) {
    $taskTokenPath = Join-Path $taskData $taskName
    if (-not (Test-Path -LiteralPath $taskTokenPath)) {
        $taskBytes = New-Object byte[] 32
        [Security.Cryptography.RandomNumberGenerator]::Fill($taskBytes)
        [IO.File]::WriteAllText($taskTokenPath,[Convert]::ToHexString($taskBytes))
    }
}
$taskConfigPath = Join-Path $taskData 'config.json'
if (-not (Test-Path -LiteralPath $taskConfigPath)) {
    $taskConfig = @{
        database=(Join-Path $taskData 'reply.db');runtime_settings_file=(Join-Path $taskRoot '.runtime-data\shared-service.json')
        worker_token_file=(Join-Path $taskData 'worker.token');control_token_file=(Join-Path $taskData 'control.token')
        harness_root=$HarnessRoot;harness_home=(Join-Path $taskData 'harness-home');node_executable=$NodeExecutable
        model='deepseek-v4-flash';poll_interval_seconds=5;settle_seconds=2;response_timeout_seconds=120
        job_ttl_seconds=300;min_reply_interval_seconds=10;max_reply_chars=500;max_tokens=2048;control_port=8767
    }
    $taskConfig | ConvertTo-Json | Set-Content -LiteralPath $taskConfigPath -Encoding utf8
}
$taskConfig = Get-Content -LiteralPath $taskConfigPath -Raw | ConvertFrom-Json
New-Item -ItemType Directory -Path $taskConfig.harness_home -Force | Out-Null
Push-Location $taskRoot
try {
    & $taskPython -c 'from tools.auto_reply.database import Store; import sys; Store(sys.argv[1])' $taskConfig.database
    if ($LASTEXITCODE -ne 0) { throw 'Automatic reply database initialization failed.' }
} finally { Pop-Location }
if ($PrepareOnly) { Write-Output 'Automatic reply private state prepared; no watcher started.'; return }
$taskName = 'Messaging Auto Reply'
$taskExisting = Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
if ($taskExisting) {
    if ($taskExisting.Actions.WorkingDirectory -ne $taskRoot) { throw 'Existing task belongs to a different installation.' }
    Stop-ScheduledTask -TaskName $taskName
}
$taskAction = New-ScheduledTaskAction -Execute $taskPythonw -Argument ('-m tools.auto_reply --config "' + $taskConfigPath + '"') -WorkingDirectory $taskRoot
$taskUser = [Security.Principal.WindowsIdentity]::GetCurrent().Name
$taskPrincipal = New-ScheduledTaskPrincipal -UserId $taskUser -LogonType Interactive -RunLevel Limited
$taskTrigger = New-ScheduledTaskTrigger -AtLogOn -User $taskUser
$taskSettings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit ([TimeSpan]::Zero) -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1) -MultipleInstances IgnoreNew -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
Register-ScheduledTask -TaskName $taskName -Action $taskAction -Principal $taskPrincipal -Trigger $taskTrigger -Settings $taskSettings -Description 'Watch explicitly enabled WeChat conversations and reply with the local DeepSeek Harness.' -Force | Out-Null
Start-ScheduledTask -TaskName $taskName
$taskDeadline = (Get-Date).AddSeconds(25)
while ((Get-Date) -lt $taskDeadline) {
    try {
        $taskHeaders = @{Authorization=('Bearer ' + (Get-Content -LiteralPath $taskConfig.control_token_file -Raw).Trim())}
        $taskStatus = Invoke-RestMethod -Uri "http://127.0.0.1:$($taskConfig.control_port)/api/status" -Headers $taskHeaders -TimeoutSec 2
        Write-Output ([PSCustomObject]@{installed=$true;task=$taskName;agent=$taskStatus.agent;model=$taskStatus.model;enabled_rules=@($taskStatus.rules | Where-Object enabled).Count} | ConvertTo-Json)
        return
    } catch { Start-Sleep -Milliseconds 500 }
}
throw 'Automatic reply worker did not start; inspect the scheduled task and local configuration.'
