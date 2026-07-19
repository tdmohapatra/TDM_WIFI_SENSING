param(
    [switch]$NoBuild
)

$ErrorActionPreference = "Stop"
$host.UI.RawUI.WindowTitle = "STAR_SENSING Engine"

$Root = Split-Path -Parent $PSScriptRoot
$EnginePath = Join-Path $Root "src\StarSensing.Engine"
$BuildCfg = if ($env:DOTNET_CONFIGURATION) { $env:DOTNET_CONFIGURATION } else { "Release" }

Set-Location -LiteralPath $Root

$runArgs = @("run", "--project", $EnginePath, "--configuration", $BuildCfg)
if ($NoBuild) { $runArgs += "--no-build" }

& dotnet @runArgs

if ($LASTEXITCODE -ne 0) {
    Write-Host ""
    Write-Host "Engine exited with code $LASTEXITCODE" -ForegroundColor Red
}

Write-Host ""
Write-Host "Press Enter to close..." -ForegroundColor Gray
Read-Host | Out-Null
