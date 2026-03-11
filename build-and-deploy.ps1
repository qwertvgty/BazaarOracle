$ErrorActionPreference = "Stop"

# Configure these paths before running.
$GameDir = "E:\SteamLibrary\steamapps\common\The Bazaar"
$ProjectDir = Join-Path $PSScriptRoot "BazaarEventLogger"
$Configuration = "Release"
$Framework = "netstandard2.1"

$ProjectFile = Join-Path $ProjectDir "BazaarEventLogger.csproj"
$BuildOutputDir = Join-Path $ProjectDir "bin\$Configuration\$Framework"
$SourceDll = Join-Path $BuildOutputDir "BazaarEventLogger.dll"
$PluginDir = Join-Path $GameDir "BepInEx\plugins"
$TargetDll = Join-Path $PluginDir "BazaarEventLogger.dll"

if (-not (Test-Path $ProjectFile)) {
    throw "Project file not found: $ProjectFile"
}

if (-not (Test-Path $GameDir)) {
    throw "Game directory not found: $GameDir"
}

if (-not (Test-Path $PluginDir)) {
    New-Item -ItemType Directory -Path $PluginDir -Force | Out-Null
}

Write-Host "Building $ProjectFile ..."
dotnet build $ProjectFile -c $Configuration

if ($LASTEXITCODE -ne 0) {
    throw "dotnet build failed with exit code $LASTEXITCODE"
}

if (-not (Test-Path $SourceDll)) {
    throw "Built DLL not found: $SourceDll"
}

Copy-Item $SourceDll $TargetDll -Force
Write-Host "Deployed DLL to $TargetDll"
