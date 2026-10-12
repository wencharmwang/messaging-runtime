<#
  装/卸 GPU 识别加速（可选）。

  为什么单独一个脚本：默认交付的包**不带** GPU 那套（CUDA 库占 2.2 GB，多数客户用不上）。
  如果你的电脑有 NVIDIA 显卡、想让识别（OCR）快一些，跑这个脚本现装就行 ——
  所有依赖都从 PyPI 下载，不需要向任何人要额外的包。

  用法（在解压出来的目录下）：
      powershell -ExecutionPolicy Bypass -File tools\install-gpu.ps1
      powershell -ExecutionPolicy Bypass -File tools\install-gpu.ps1 -Check      # 只看状态
      powershell -ExecutionPolicy Bypass -File tools\install-gpu.ps1 -Remove     # 卸掉，回到 CPU

  装完要重启一次消息助手才会生效（识别进程在启动时才读 provider 列表）。
#>
param(
  [switch]$Check,
  [switch]$Remove,
  # 没检测到 NVIDIA 显卡时仍然要装（通常没意义，但客户可能自己清楚）。
  [switch]$Force
)

$ErrorActionPreference = 'Stop'
function Say($t, $c = 'Gray') { Write-Host $t -ForegroundColor $c }

$RuntimeRoot  = Split-Path $PSScriptRoot -Parent
$pkgPython    = Join-Path $RuntimeRoot 'runtime\python\python.exe'
$sitePackages = Join-Path $RuntimeRoot 'runtime\python\Lib\site-packages'
$gpuLibs      = Join-Path $RuntimeRoot 'runtime\gpu-libs'
$cpuBackup    = Join-Path $RuntimeRoot 'runtime\onnxruntime-cpu-backup'
$pthFile      = Join-Path $sitePackages '_gpu_libs.pth'
$reqFile      = Join-Path $PSScriptRoot 'gpu-requirements.txt'

if (-not (Test-Path -LiteralPath $pkgPython)) {
  Say "找不到自带的 Python：$pkgPython" 'Red'
  Say '请确认是在解压出来的包目录里运行的（不要直接在压缩包里跑）。' 'Yellow'
  exit 1
}

function Get-Providers {
  $code = "import onnxruntime as o; print(','.join(o.get_available_providers()))"
  return (& $pkgPython -c $code 2>&1 | Select-Object -Last 1)
}

function Test-NvidiaGpu {
  $smi = Get-Command 'nvidia-smi' -ErrorAction SilentlyContinue
  if (-not $smi) { return $false }
  try { return (& $smi.Source -L 2>$null | Select-String -Pattern 'GPU' -Quiet) } catch { return $false }
}

# ---- 只看状态 ----
if ($Check) {
  Say ''
  Say '== GPU 加速状态' 'Cyan'
  Say ("   NVIDIA 显卡：{0}" -f $(if (Test-NvidiaGpu) { '检测到' } else { '没有检测到（或没装驱动）' }))
  Say ("   加速库目录：{0}" -f $(if (Test-Path -LiteralPath $gpuLibs) { '已安装' } else { '未安装（当前用 CPU）' }))
  Say ("   onnxruntime providers：{0}" -f (Get-Providers))
  Say ''
  Say '   想要加速就跑：tools\install-gpu.ps1' 'DarkGray'
  exit 0
}

# ---- 卸载：回到 CPU ----
if ($Remove) {
  Say ''
  Say '== 卸载 GPU 加速' 'Cyan'
  if (Test-Path -LiteralPath $gpuLibs) { Microsoft.PowerShell.Management\Remove-Item -LiteralPath $gpuLibs -Recurse -Force }
  if (Test-Path -LiteralPath $pthFile) { Microsoft.PowerShell.Management\Remove-Item -LiteralPath $pthFile -Force }
  if (Test-Path -LiteralPath $cpuBackup) {
    foreach ($item in @(Get-ChildItem -LiteralPath $cpuBackup)) {
      Move-Item -LiteralPath $item.FullName -Destination $sitePackages -Force
    }
    Microsoft.PowerShell.Management\Remove-Item -LiteralPath $cpuBackup -Recurse -Force -ErrorAction SilentlyContinue
    Say '   已把 CPU 版 onnxruntime 放回原位' 'Gray'
  }
  Say ("   现在 providers：{0}" -f (Get-Providers))
  Say '   已回到 CPU 模式。重启消息助手后生效。' 'Green'
  exit 0
}

# ---- 安装 ----
Say ''
Say '== 安装 GPU 识别加速' 'Cyan'

if (-not (Test-NvidiaGpu)) {
  Say '   没有检测到 NVIDIA 显卡（或驱动没装）。' 'Yellow'
  Say '   继续装也不会更快；确认要继续就再跑一次并加 -Force。' 'Yellow'
  if (-not $Force) { exit 2 }
}

if (-not (Test-Path -LiteralPath $reqFile)) {
  Say "   找不到依赖清单：$reqFile" 'Red'; exit 1
}
$pins = @(Get-Content -LiteralPath $reqFile | Where-Object { $_.Trim() -ne '' })
Say ("   要装 {0} 个包（从 PyPI 下载，约 2 GB，请耐心等）…" -f $pins.Count) 'Gray'

# 1) 先把 CPU 版 onnxruntime 挪开：它和 GPU 版同名，留着会挡住 GPU 版生效。
#    挪到旁边的备份目录而不是删掉 —— 这样 -Remove 能干净地还原。
$cpu = Join-Path $sitePackages 'onnxruntime'
if (Test-Path -LiteralPath $cpu) {
  New-Item -ItemType Directory -Force -Path $cpuBackup | Out-Null
  Move-Item -LiteralPath $cpu -Destination (Join-Path $cpuBackup 'onnxruntime') -Force
  foreach ($info in @(Get-ChildItem -LiteralPath $sitePackages -Directory -Filter 'onnxruntime-*.dist-info')) {
    Move-Item -LiteralPath $info.FullName -Destination $cpuBackup -Force
  }
  Say '   已把 CPU 版 onnxruntime 移开（要还原就跑 -Remove）' 'Gray'
}

# 2) 装到独立目录，再用 .pth 加进 sys.path —— 不动 site-packages 里别的东西。
New-Item -ItemType Directory -Force -Path $gpuLibs | Out-Null
$previous = $ErrorActionPreference
$ErrorActionPreference = 'Continue'   # pip 往 stderr 写进度，Stop 会把它当致命错误
& $pkgPython -m pip install --target $gpuLibs --only-binary :all: --upgrade --requirement $reqFile
$pipCode = $LASTEXITCODE
$ErrorActionPreference = $previous
if ($pipCode -ne 0) {
  Say ("   安装失败（pip 退出码 {0}）。" -f $pipCode) 'Red'
  Say '   常见原因：网络不通 / 磁盘空间不够（需要约 2.5 GB 空闲）。' 'Yellow'
  Say '   已装了一半也不影响使用：CPU 版还在备份里，跑 -Remove 可以还原。' 'Yellow'
  exit $pipCode
}
[IO.File]::WriteAllText($pthFile, $gpuLibs, [Text.UTF8Encoding]::new($false))

# 3) 验收：看 provider 里有没有 CUDA
$providers = Get-Providers
Say ("   现在 providers：{0}" -f $providers)
if ($providers -match 'CUDA') {
  Say '   加速已生效。重启消息助手后识别会走 GPU。' 'Green'
} else {
  Say '   装完了，但没看到 CUDA provider —— 可能是驱动版本与 CUDA 运行时不匹配。' 'Yellow'
  Say '   先重启消息助手再看：tools\install-gpu.ps1 -Check' 'Yellow'
}
