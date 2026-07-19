@echo off
cd /d "%~dp0"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Start-StarSensing.ps1" %*
if errorlevel 1 (
    echo.
    echo Start failed. See messages above.
    pause
)
