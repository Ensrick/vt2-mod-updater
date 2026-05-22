# Builds a distributable single-file Release exe of VT2ModUpdater.
# Output: src/VT2ModUpdater/bin/Release/net9.0-windows/win-x64/publish/VT2ModUpdater.exe
[CmdletBinding()]
param(
    [switch]$SkipOpen
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$sln  = Join-Path $root 'vt2-mod-updater.sln'
$proj = Join-Path $root 'src\VT2ModUpdater\VT2ModUpdater.csproj'

Write-Host "==> dotnet test (gate the publish on a green test suite)"
dotnet test $sln -c Release --logger "console;verbosity=normal"
if ($LASTEXITCODE -ne 0) { throw "dotnet test failed (exit $LASTEXITCODE) — refusing to publish a broken build" }

Write-Host ""
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
