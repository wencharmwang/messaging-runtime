param(
  [switch]$SkipBuild,
  # 交付给客户时带上这个：把 .NET 运行时一起打进去，客户机不用装 .NET。
  # 用它就不需要客户机有任何前置（除了微信本身）。
  [switch]$SelfContained,
  # 不带 GPU 加速（默认给客户的形态）：去掉 nvidia\ 那 2.2 GB 的 CUDA 库，
  # 并把 onnxruntime 换成 CPU 版。识别仍然能用，只是慢一些。
  # 客户以后想要加速时，可以自己跑 tools\install-gpu.ps1 现装（从 PyPI，不需要我们托管）。
  [switch]$SkipGpu
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

# ---- GPU 加速：默认不带，客户以后自己现装 ----
# nvidia\ 那套 CUDA 库占 2.2 GB，而多数客户要么没独显、要么不需要那么快的识别。
# 所以：默认发**不带 GPU** 的包（onnxruntime 换成 CPU 版），把要装的东西写成清单，
# 客户想要加速时跑 tools\install-gpu.ps1 —— 从 PyPI 现装，我们不托管任何二进制。
$pkgPython    = Join-Path $packagePath 'runtime/python/python.exe'
$sitePackages = Join-Path $packagePath 'runtime/python/Lib/site-packages'
$gpuListPath  = Join-Path $packagePath 'tools/gpu-requirements.txt'

# 清单按**当前在用的版本**生成 —— 客户装到的就是这台机器上验证过的那一套。
$pins = @()
foreach ($line in @(& $pkgPython -m pip list 2>$null)) {
  if ($line -match '^(nvidia-[A-Za-z0-9_.\-]+)\s+(\S+)') {
    $pins += ('{0}=={1}' -f $Matches[1], $Matches[2])
  } elseif ($line -match '^(onnxruntime-gpu)\s+(\S+)') {
    $pins += ('{0}=={1}' -f $Matches[1], $Matches[2])
  }
}
if ($pins.Count -eq 0) {
  # 兜底：在"已经是 CPU 版"的包上再打包时（本机不再有 -gpu 那套），写已知可用的版本。
  $pins = @(
    'onnxruntime-gpu==1.22.0',
    'nvidia-cublas-cu12==12.9.2.10',
    'nvidia-cuda-runtime-cu12==12.9.79',
    'nvidia-cudnn-cu12==9.26.0.51',
    'nvidia-cufft-cu12==11.4.1.4',
    'nvidia-curand-cu12==10.3.10.19'
  )
}
[IO.File]::WriteAllLines($gpuListPath, $pins, [Text.UTF8Encoding]::new($false))
Write-Output ("GPU 清单：tools/gpu-requirements.txt（{0} 项）" -f $pins.Count)

if ($SkipGpu) {
  $nvidiaDir = Join-Path $sitePackages 'nvidia'
  if (Test-Path -LiteralPath $nvidiaDir) {
    $before = [math]::Round(((Get-ChildItem -LiteralPath $nvidiaDir -Recurse -File | Measure-Object Length -Sum).Sum / 1MB), 0)
    Microsoft.PowerShell.Management\Remove-Item -LiteralPath $nvidiaDir -Recurse -Force
    Write-Output ("已去掉 GPU 库：nvidia\ 约省 {0} MB" -f $before)
  }
  # onnxruntime-gpu 在没有 CUDA 库时不保证能干净退到 CPU，所以直接换成 CPU 版更稳。
  $cpu = Join-Path $sitePackages 'onnxruntime'
  if (Test-Path -LiteralPath $cpu) { Microsoft.PowerShell.Management\Remove-Item -LiteralPath $cpu -Recurse -Force }
  foreach ($info in @(Get-ChildItem -LiteralPath $sitePackages -Directory -Filter 'onnxruntime_gpu-*.dist-info')) {
    Microsoft.PowerShell.Management\Remove-Item -LiteralPath $info.FullName -Recurse -Force
  }
  $log = Join-Path $env:TEMP 'gpu-swap-cpu.log'
  $previous = $ErrorActionPreference
  $ErrorActionPreference = 'Continue'   # pip 会往 stderr 写进度，Stop 会把它当致命错误
  & $pkgPython -m pip install --target $sitePackages --only-binary :all: --upgrade 'onnxruntime==1.22.0' 2>&1 |
    ForEach-Object { Add-Content -LiteralPath $log -Value $_ }
  $pipCode = $LASTEXITCODE
  $ErrorActionPreference = $previous
  if ($pipCode -ne 0) { throw "把 onnxruntime 换成 CPU 版失败，见 $log" }
  Write-Output '已把 onnxruntime 换成 CPU 版（识别可用，速度较慢；要加速跑 tools\install-gpu.ps1）'
}

Write-Output "Package created: $packagePath"
