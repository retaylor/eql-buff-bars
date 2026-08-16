# publish.ps1 - build the shippable single-file EqlBuffBars.exe into dist/.
# PowerShell 5.1 compatible. Run from anywhere; paths resolve relative to the repo root.

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot

# Locate dotnet: DOTNET_ROOT first, then the user-local install, then PATH.
$dotnet = $null
if ($env:DOTNET_ROOT) {
    $candidate = Join-Path $env:DOTNET_ROOT 'dotnet.exe'
    if (Test-Path $candidate) { $dotnet = $candidate }
}
if (-not $dotnet) {
    $candidate = Join-Path $env:USERPROFILE '.dotnet\dotnet.exe'
    if (Test-Path $candidate) { $dotnet = $candidate }
}
if (-not $dotnet) {
    $cmd = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($cmd) { $dotnet = $cmd.Source }
}
if (-not $dotnet) {
    Write-Error 'dotnet SDK not found. Set DOTNET_ROOT, install to %USERPROFILE%\.dotnet, or add dotnet to PATH.'
}

Write-Output "Using dotnet: $dotnet"

$appProject = Join-Path $repoRoot 'src\BuffBars.App'
$distDir = Join-Path $repoRoot 'dist'

& $dotnet publish $appProject -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -o $distDir
if ($LASTEXITCODE -ne 0) {
    Write-Error "dotnet publish failed with exit code $LASTEXITCODE"
}

$exePath = Join-Path $distDir 'EqlBuffBars.exe'
if (-not (Test-Path $exePath)) {
    Write-Error "Publish reported success but $exePath was not found."
}

$hash = Get-FileHash -Path $exePath -Algorithm SHA256
$sizeMb = [math]::Round((Get-Item $exePath).Length / 1MB, 1)

Write-Output ''
Write-Output "Published : $exePath ($sizeMb MB)"
Write-Output "SHA256    : $($hash.Hash)"
