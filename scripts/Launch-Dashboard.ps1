param(
    [switch]$NoBuild
)

$ErrorActionPreference = "Stop"
$host.UI.RawUI.WindowTitle = "STAR_SENSING Dashboard"

$Root = Split-Path -Parent $PSScriptRoot
$DashPath = Join-Path $Root "src\StarSensing.Dashboard"
$BuildCfg = if ($env:DOTNET_CONFIGURATION) { $env:DOTNET_CONFIGURATION } else { "Release" }

Set-Location -LiteralPath $Root

$runArgs = @("run", "--project", $DashPath, "--configuration", $BuildCfg)
if ($NoBuild) { $runArgs += "--no-build" }

& dotnet @runArgs

if ($LASTEXITCODE -ne 0) {
    Write-Host ""
    Write-Host "Dashboard exited with code $LASTEXITCODE" -ForegroundColor Red
}

Write-Host ""
Write-Host "Press Enter to close..." -ForegroundColor Gray
Read-Host | Out-Null
