[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",

    [switch]$SkipTests,
    [switch]$Publish,
    [switch]$Standalone
)

$ErrorActionPreference = "Stop"

Write-Host "============================================================" -ForegroundColor Cyan
Write-Host " ControllerAudioSwitcher - DualShock 4 Audio Streamer Build " -ForegroundColor Cyan
Write-Host "============================================================" -ForegroundColor Cyan

$DotnetExe = "dotnet"
if (Test-Path "$env:USERPROFILE\.dotnet\dotnet.exe") {
    $DotnetExe = "$env:USERPROFILE\.dotnet\dotnet.exe"
    $env:PATH = "$env:USERPROFILE\.dotnet;" + $env:PATH
}

Write-Host "[1/4] Checking .NET SDK..." -ForegroundColor Yellow
& $DotnetExe --version
if ($LASTEXITCODE -ne 0) {
    Write-Error "Failed to locate a working .NET SDK."
    exit 1
}

Write-Host "[2/4] Restoring & Building Solution ($Configuration)..." -ForegroundColor Yellow
& $DotnetExe build ControllerAudioSwitcher.sln -c $Configuration
if ($LASTEXITCODE -ne 0) {
    Write-Error "Build failed with exit code $LASTEXITCODE."
    exit $LASTEXITCODE
}
Write-Host "[OK] Build succeeded." -ForegroundColor Green

if (-not $SkipTests) {
    Write-Host "[3/4] Running Automated Tests..." -ForegroundColor Yellow
    & $DotnetExe test tests/ControllerAudioSwitcher.Core.Tests/ControllerAudioSwitcher.Core.Tests.csproj -c $Configuration --no-build --logger "console;verbosity=normal"
    if ($LASTEXITCODE -ne 0) {
        Write-Error "Tests failed with exit code $LASTEXITCODE."
        exit $LASTEXITCODE
    }
    Write-Host "[OK] All test suites passed." -ForegroundColor Green
}
else {
    Write-Host "[3/4] Tests skipped as requested." -ForegroundColor DarkGray
}

if ($Publish) {
    Write-Host "[4/5] Publishing Windows 11 Desktop Application (Framework-dependent)..." -ForegroundColor Yellow
    $OutputDir = "$PSScriptRoot\bin\publish"
    & $DotnetExe publish src/ControllerAudioSwitcher.App/ControllerAudioSwitcher.App.csproj -c $Configuration -r win-x64 --self-contained false -o $OutputDir
    if ($LASTEXITCODE -ne 0) {
        Write-Error "Publish failed with exit code $LASTEXITCODE."
        exit $LASTEXITCODE
    }
    Write-Host "[OK] Application published successfully to: $OutputDir" -ForegroundColor Green
    Write-Host "Executable: $OutputDir\ControllerAudioSwitcher.App.exe" -ForegroundColor Cyan
}

if ($Standalone) {
    Write-Host "[5/5] Building Self-Contained Standalone Single-File Executable..." -ForegroundColor Yellow
    $StandaloneDir = "$PSScriptRoot\bin\standalone"
    & $DotnetExe publish src/ControllerAudioSwitcher.App/ControllerAudioSwitcher.App.csproj `
        -c $Configuration `
        -r win-x64 `
        --self-contained true `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:EnableCompressionInSingleFile=true `
        -p:DebugType=None `
        -o $StandaloneDir
    if ($LASTEXITCODE -ne 0) {
        Write-Error "Standalone publish failed with exit code $LASTEXITCODE."
        exit $LASTEXITCODE
    }
    if (Test-Path "$StandaloneDir\ControllerAudioSwitcher.App.exe") {
        Copy-Item "$StandaloneDir\ControllerAudioSwitcher.App.exe" "$StandaloneDir\ControllerAudioSwitcher.exe" -Force
    }
    Write-Host "[OK] Standalone Single-File Executable built successfully to: $StandaloneDir" -ForegroundColor Green
    Write-Host "Standalone Binary: $StandaloneDir\ControllerAudioSwitcher.exe" -ForegroundColor Cyan
}

Write-Host "All operations completed successfully." -ForegroundColor Green
