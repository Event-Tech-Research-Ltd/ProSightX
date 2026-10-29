# Builds a standalone ProSight.exe for Windows: one file, .NET runtime included, nothing to install.
#   .\build-windows.ps1                   → 64-bit Intel/AMD (win-x64)
#   .\build-windows.ps1 -Runtime win-arm64 → Windows on Arm (e.g. Snapdragon laptops)
# Needs the .NET 8 SDK on the build machine only. Output: dist\<rid>\ProSight\ProSight.exe and a .zip.
# Or double-click build-windows.cmd.
param([ValidateSet('win-x64', 'win-arm64')][string]$Runtime = 'win-x64')
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw 'The .NET 8 SDK is required to build: https://dotnet.microsoft.com/download'
}

[xml]$props = Get-Content 'Directory.Build.props'
$version = $props.Project.PropertyGroup.Version
$out = Join-Path 'dist' $Runtime
$app = Join-Path $out 'ProSight'
if (Test-Path $out) { Remove-Item $out -Recurse -Force }

Write-Host "-> Publishing ProSight $version for $Runtime (self-contained, single file)"
# Windows Forms doesn't support trimming, so no PublishTrimmed. Compression keeps the exe to a practical size.
dotnet publish 'ProSight\ProSight.csproj' -c Release -r $Runtime --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none -o $app
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit $LASTEXITCODE)" }

$exe = Join-Path $app 'ProSight.exe'
if (-not (Test-Path $exe)) { throw "Expected $exe was not produced" }
Copy-Item 'README.md' $app

$zip = Join-Path $out "ProSight-$version-$Runtime.zip"
Compress-Archive -Path (Join-Path $app '*') -DestinationPath $zip -Force

$sizeMb = [math]::Round((Get-Item $exe).Length / 1MB, 1)
Write-Host "OK  $exe  ($sizeMb MB)"
Write-Host "OK  $zip"
Write-Host "Check it on this PC:  $exe --selftest"
