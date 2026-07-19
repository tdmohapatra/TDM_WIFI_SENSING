$ErrorActionPreference = "Stop"
$host.UI.RawUI.WindowTitle = "STAR_SENSING Python"

$Root = Split-Path -Parent $PSScriptRoot
$PyPath = Join-Path $Root "src\StarSensing.Python"
$PyVenv = Join-Path $PyPath "venv\Scripts\python.exe"

Set-Location -LiteralPath $PyPath
& $PyVenv server.py

if ($LASTEXITCODE -ne 0) {
    Write-Host ""
    Write-Host "Python service exited with code $LASTEXITCODE" -ForegroundColor Red
}

Write-Host ""
Write-Host "Press Enter to close..." -ForegroundColor Gray
Read-Host | Out-Null
