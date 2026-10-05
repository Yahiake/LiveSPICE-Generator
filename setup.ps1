# setup.ps1 - Setup script for LiveSPICE-Generator
param(
    [string]$LiveSpiceRepo = "https://github.com/dsharlet/LiveSPICE.git"
)

$ErrorActionPreference = "Stop"

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Definition
$thirdPartyDir = Join-Path $scriptDir "third_party"
$liveSpiceDir = Join-Path $thirdPartyDir "LiveSPICE"
$siblingLiveSpice = Join-Path (Split-Path -Parent $scriptDir) "LiveSPICE"

Write-Host "=== Setting up LiveSPICE-Generator ===" -ForegroundColor Cyan

$resolvedLiveSpice = $null

if (Test-Path (Join-Path $liveSpiceDir "Circuit\Circuit.csproj")) {
    Write-Host "Found LiveSPICE at $liveSpiceDir" -ForegroundColor Green
    $resolvedLiveSpice = $liveSpiceDir
} elseif (Test-Path (Join-Path $siblingLiveSpice "Circuit\Circuit.csproj")) {
    Write-Host "Found sibling LiveSPICE at $siblingLiveSpice" -ForegroundColor Green
    $resolvedLiveSpice = $siblingLiveSpice
} else {
    Write-Host "LiveSPICE not found locally. Cloning recursively from $LiveSpiceRepo..." -ForegroundColor Yellow
    if (!(Test-Path $thirdPartyDir)) {
        New-Item -ItemType Directory -Path $thirdPartyDir -Force | Out-Null
    }
    git clone --recurse-submodules --depth 1 $LiveSpiceRepo $liveSpiceDir
    if ($LASTEXITCODE -ne 0) {
        Write-Error "Failed to clone LiveSPICE."
        exit 1
    }
    Write-Host "Successfully cloned LiveSPICE into $liveSpiceDir" -ForegroundColor Green
    $resolvedLiveSpice = $liveSpiceDir
}

# Verify ComputerAlgebra submodule
$computerAlgebraPath = Join-Path $resolvedLiveSpice "ComputerAlgebra\ComputerAlgebra\ComputerAlgebra.csproj"
if (!(Test-Path $computerAlgebraPath)) {
    Write-Host "ComputerAlgebra submodule missing. Initializing submodules..." -ForegroundColor Yellow
    git -C $resolvedLiveSpice submodule update --init --recursive
    if (!(Test-Path $computerAlgebraPath)) {
        Write-Error "Failed to initialize ComputerAlgebra submodule at $computerAlgebraPath."
        exit 1
    }
    Write-Host "Successfully initialized submodules." -ForegroundColor Green
}

Write-Host "Building LiveSPICE-Generator..." -ForegroundColor Cyan
dotnet build (Join-Path $scriptDir "LiveSPICE-Generator.sln")
if ($LASTEXITCODE -eq 0) {
    Write-Host "Build succeeded! You can now run livespice-gen." -ForegroundColor Green
} else {
    Write-Error "Build failed. Please verify .NET SDK installation."
    exit 1
}
