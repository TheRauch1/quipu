<#
.SYNOPSIS
  Tests and builds Quipu.exe in Docker (no local .NET SDK needed) and optionally installs/starts it.

.EXAMPLE
  .\build.ps1                                  # test, then build -> .\dist\Quipu.exe
  .\build.ps1 -SkipTests                       # build only
  .\build.ps1 -Install -Run                    # build, copy to %USERPROFILE%\Quipu, (re)start it
  .\build.ps1 -Install -InstallDir D:\llama    # install somewhere else
#>
param(
    [switch]$Install,
    [string]$InstallDir = (Join-Path $env:USERPROFILE "Quipu"),
    [string]$Version = "1.0.0",
    [switch]$SkipTests,
    [switch]$Run
)
# Not "Stop": Windows PowerShell 5.1 turns docker's progress output (stderr) into terminating errors.
# Failures are detected through $LASTEXITCODE instead.
$ErrorActionPreference = "Continue"
Set-Location $PSScriptRoot

if (-not $SkipTests) {
    Write-Host "Running unit tests in Docker..." -ForegroundColor Cyan
    docker build --target test .
    if ($LASTEXITCODE -ne 0) { throw "unit tests failed" }
}

Write-Host "Building Quipu $Version (win-x64, self-contained) in Docker..." -ForegroundColor Cyan
docker build --build-arg VERSION=$Version --target export --output "type=local,dest=dist" .
if ($LASTEXITCODE -ne 0) { throw "docker build failed" }
$exe = Join-Path $PSScriptRoot "dist\Quipu.exe"
Write-Host ("Built {0} ({1:N0} MB)" -f $exe, ((Get-Item $exe).Length / 1MB)) -ForegroundColor Green

if ($Install) {
    New-Item -ItemType Directory -Force $InstallDir | Out-Null
    $running = Get-Process Quipu -ErrorAction SilentlyContinue
    if ($running) {
        Write-Host "Stopping running Quipu (its llama-server instances stop too)..." -ForegroundColor Yellow
        $running | Stop-Process -Force
        Start-Sleep -Seconds 1
    }
    Copy-Item $exe (Join-Path $InstallDir "Quipu.exe") -Force
    Copy-Item (Join-Path $PSScriptRoot "dist\THIRD-PARTY-NOTICES.txt") $InstallDir -Force
    Copy-Item (Join-Path $PSScriptRoot "dist\LICENSE") $InstallDir -Force
    Write-Host "Installed to $InstallDir\Quipu.exe" -ForegroundColor Green
    $exe = Join-Path $InstallDir "Quipu.exe"
}

if ($Run) {
    Start-Process $exe
    Write-Host "Quipu started - dashboard: http://127.0.0.1:5250" -ForegroundColor Green
}
