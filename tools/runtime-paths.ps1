$MessagingProjectRoot = Split-Path -Parent $PSScriptRoot
function Get-MessagingDotnet {
    $command = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }
    $localDotnet = Join-Path $env:USERPROFILE '.dotnet/dotnet.exe'
    if (Test-Path -LiteralPath $localDotnet -PathType Leaf) { return $localDotnet }
    throw '.NET 8 SDK/runtime was not found. Install it or add dotnet to PATH.'
}
