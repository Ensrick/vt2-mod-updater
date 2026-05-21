# Builds a distributable single-file Release exe of VT2ModUpdater.
# Output: src/VT2ModUpdater/bin/Release/net9.0-windows/win-x64/publish/VT2ModUpdater.exe
[CmdletBinding()]
param(
    [switch]$SkipOpen
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$proj = Join-Path $root 'src\VT2ModUpdater\VT2ModUpdater.csproj'

Write-Host "==> dotnet publish -c Release"
dotnet publish $proj -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit $LASTEXITCODE)" }

$publishDir = Join-Path $root 'src\VT2ModUpdater\bin\Release\net9.0-windows\win-x64\publish'
$exe = Join-Path $publishDir 'VT2ModUpdater.exe'
if (-not (Test-Path $exe)) { throw "Expected exe not found at $exe" }

$size = (Get-Item $exe).Length / 1MB
Write-Host ""
Write-Host "Built: $exe"
Write-Host ("Size: {0:N1} MB" -f $size)

if (-not $SkipOpen) {
    Start-Process explorer.exe $publishDir
}
