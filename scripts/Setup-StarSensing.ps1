<#
.SYNOPSIS
    Preflight check and optional auto-setup for STAR_SENSING.

.DESCRIPTION
    Verifies .NET SDKs, Python, SQL Server, ODBC driver, Python venv, and ML models.
    If anything is missing, prompts for the install password (default 7787 from .env)
    and installs/fixes what it can via winget, pip, and dotnet.

.PARAMETER CheckOnly
    Only report status; do not install or fix anything.

.PARAMETER ForceInstall
    Skip password prompt (use only in trusted automation).

.EXAMPLE
    .\Setup-StarSensing.ps1
    .\Setup-StarSensing.ps1 -CheckOnly
#>

param(
    [switch]$CheckOnly,
    [switch]$ForceInstall
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$Root = Split-Path -Parent $PSScriptRoot
if (-not (Test-Path (Join-Path $Root ".env"))) {
    $Root = $PSScriptRoot
}

. (Join-Path $PSScriptRoot "Load-DotEnv.ps1")
$null = Import-DotEnv -EnvFile (Join-Path $Root ".env")

$EnginePath   = Join-Path $Root "src\StarSensing.Engine"
$DashPath     = Join-Path $Root "src\StarSensing.Dashboard"
$PyPath       = Join-Path $Root "src\StarSensing.Python"
$PyVenv       = Join-Path $PyPath "venv\Scripts\python.exe"
$PyPip        = Join-Path $PyPath "venv\Scripts\pip.exe"
$ReqFile      = Join-Path $PyPath "requirements.txt"
function Get-EnvDefault([string]$Key, [string]$Default) {
    $val = [Environment]::GetEnvironmentVariable($Key)
    if ($val) { return $val }
    return $Default
}

$ModelsDir    = Join-Path $PyPath (Get-EnvDefault "PYTHON_MODELS_DIR" "models")
$InstallPwd   = Get-EnvDefault "SETUP_INSTALL_PASSWORD" "7787"
$EngineSdk    = [int](Get-EnvDefault "DOTNET_ENGINE_SDK_MAJOR" "8")
$DashSdk      = [int](Get-EnvDefault "DOTNET_DASHBOARD_SDK_MAJOR" "9")
$PyMinMajor   = [int](Get-EnvDefault "PYTHON_MIN_MAJOR" "3")
$PyMinMinor   = [int](Get-EnvDefault "PYTHON_MIN_MINOR" "10")
$SqlCs        = $env:SQL_CONNECTION_STRING

$script:Issues = [System.Collections.Generic.List[string]]::new()

function Register-Issue([string]$msg) {
    Write-Fail $msg
}

function Invoke-ApplyFixes {
    Write-Head "Applying fixes"

    if (-not (Test-DotNetSdk -Major $EngineSdk)) {
        $id = Get-EnvDefault "WINGET_DOTNET8" "Microsoft.DotNet.SDK.8"
        try { Install-WingetPackage $id ".NET SDK 8" } catch { Write-Host "  [ERROR] $_" -ForegroundColor Red }
    }

    if (-not (Test-DotNetSdk -Major $DashSdk)) {
        $id9 = Get-EnvDefault "WINGET_DOTNET9" "Microsoft.DotNet.SDK.9"
        try { Install-WingetPackage $id9 ".NET SDK 9" } catch { Write-Host "  [ERROR] $_" -ForegroundColor Red }
    }

    $pyOk, $null = Test-PythonVersion
    if (-not $pyOk) {
        $pyId = Get-EnvDefault "WINGET_PYTHON" "Python.Python.3.12"
        try {
            Install-WingetPackage $pyId "Python 3.12"
            $env:Path = [System.Environment]::GetEnvironmentVariable("Path", "Machine") + ";" +
                        [System.Environment]::GetEnvironmentVariable("Path", "User")
        }
        catch { Write-Host "  [ERROR] $_" -ForegroundColor Red }
    }

    if (-not (Test-OdbcDriver17)) {
        $odbcId = Get-EnvDefault "WINGET_ODBC17" "Microsoft.msodbcsql.17"
        try { Install-WingetPackage $odbcId "ODBC Driver 17" } catch { Write-Host "  [ERROR] $_" -ForegroundColor Red }
    }

    if (-not (Test-PythonVenv)) {
        try {
            Push-Location $PyPath
            & python -m venv venv
            Pop-Location
            Write-Pass "Created Python venv"
        }
        catch { Write-Host "  [ERROR] $_" -ForegroundColor Red }
    }

    if (-not (Test-PythonDeps)) {
        try {
            if (-not (Test-Path $PyPip)) {
                Push-Location $PyPath
                & python -m venv venv
                Pop-Location
            }
            Write-Info "pip install (may take several minutes)..."
            & $PyPip install --upgrade pip
            & $PyPip install -r $ReqFile
            Write-Pass "Python packages installed"
        }
        catch { Write-Host "  [ERROR] $_" -ForegroundColor Red }
    }

    if (-not (Test-MlModels) -and (Test-Path $PyVenv)) {
        $epochs = [int](Get-EnvDefault "PYTHON_TRAIN_EPOCHS" "8")
        try {
            Write-Info "Training initial models ($epochs epochs)..."
            Push-Location $PyPath
            & $PyVenv train_models.py --epochs $epochs --models-dir $ModelsDir
            Pop-Location
            Write-Pass "Model training completed"
        }
        catch { Write-Host "  [ERROR] $_" -ForegroundColor Red }
    }

    if (-not (Test-SqlServer)) {
        Write-Host "  [ACTION REQUIRED] Start SQL Server and update SQL_CONNECTION_STRING in .env" -ForegroundColor Yellow
        Write-Host "  Express: Server=localhost\SQLEXPRESS;Database=StarSensing;Trusted_Connection=True;TrustServerCertificate=True;" -ForegroundColor Yellow
    }
}

function Write-Head([string]$msg) {
    Write-Host ""
    Write-Host "  >> $msg" -ForegroundColor Cyan
}

function Write-Pass([string]$msg) { Write-Host "  [OK] $msg" -ForegroundColor Green }
function Write-Fail([string]$msg) {
    Write-Host "  [MISSING] $msg" -ForegroundColor Red
    $script:Issues.Add($msg)
}

function Write-Info([string]$msg) { Write-Host "  [INFO] $msg" -ForegroundColor DarkGray }

function Test-Admin {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    $p = New-Object Security.Principal.WindowsPrincipal($id)
    return $p.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Test-Winget {
    return [bool](Get-Command winget -ErrorAction SilentlyContinue)
}

function Test-DotNetSdk([int]$Major) {
    try {
        $sdks = & dotnet --list-sdks 2>$null
        if (-not $sdks) { return $false }
        foreach ($line in $sdks) {
            if ($line -match "^$Major\.") { return $true }
        }
        return $false
    }
    catch { return $false }
}

function Test-PythonVersion {
    $candidates = @()
    $sysPy = Get-Command python -ErrorAction SilentlyContinue
    if ($sysPy) { $candidates += $sysPy.Source }
    if (Test-Path $PyVenv) { $candidates += $PyVenv }

    foreach ($exe in $candidates | Select-Object -Unique) {
        try {
            $ver = & $exe -c "import sys; print(str(sys.version_info.major) + '.' + str(sys.version_info.minor))"
            $parts = $ver.Trim().Split('.')
            $maj = [int]$parts[0]; $min = [int]$parts[1]
            if (($maj -gt $PyMinMajor) -or ($maj -eq $PyMinMajor -and $min -ge $PyMinMinor)) {
                return $true, $ver.Trim()
            }
        }
        catch { continue }
    }
    return $false, $null
}

function Test-SqlServer {
    if (-not $SqlCs) { return $false }
    try {
        Add-Type -AssemblyName "System.Data" -ErrorAction Stop
        try {
            $conn = New-Object System.Data.SqlClient.SqlConnection($SqlCs)
            $conn.Open()
            $conn.Close()
            return $true
        }
        catch {
            $masterCs = $SqlCs -replace 'Database=[^;]+', 'Database=master'
            $conn = New-Object System.Data.SqlClient.SqlConnection($masterCs)
            $conn.Open()
            $conn.Close()
            return $true
        }
    }
    catch { return $false }
}

function Test-OdbcDriver17 {
    try {
        $keys = @(
            "HKLM:\SOFTWARE\ODBC\ODBCINST.INI\ODBC Driver 17 for SQL Server",
            "HKLM:\SOFTWARE\WOW6432Node\ODBC\ODBCINST.INI\ODBC Driver 17 for SQL Server"
        )
        foreach ($k in $keys) {
            if (Test-Path $k) { return $true }
        }
        return $false
    }
    catch { return $false }
}

function Test-PythonVenv {
    return (Test-Path $PyVenv) -and (Test-Path $PyPip)
}

function Test-PythonDeps {
    if (-not (Test-Path $PyVenv)) { return $false }
    try {
        & $PyVenv -c "import grpc, numpy, sklearn, yaml" 2>&1 | Out-Null
        return $LASTEXITCODE -eq 0
    }
    catch { return $false }
}

function Test-MlModels {
    $names = @("lstm_motion.keras", "lstm_motion.h5", "lstm_motion.joblib")
    foreach ($n in $names) {
        if (Test-Path (Join-Path $ModelsDir $n)) { return $true }
    }
    return $false
}

function Test-WiFiAdapter {
    try {
        $adapters = Get-NetAdapter -Physical -ErrorAction SilentlyContinue |
            Where-Object { $_.Status -eq 'Up' -and $_.MediaType -match '802.11|Native 802.11|Wi-Fi' }
        return ($null -ne $adapters -and @($adapters).Count -gt 0)
    }
    catch {
        return $null  # unknown
    }
}

function Request-InstallPassword {
    if ($ForceInstall) { return $true }
    Write-Host ""
    Write-Host "  Some requirements are missing. Auto-install needs authorization." -ForegroundColor Yellow
    Write-Host "  Enter setup install password (see .env SETUP_INSTALL_PASSWORD):" -ForegroundColor Yellow
    $secure = Read-Host "  Password" -AsSecureString
    $plain = [Runtime.InteropServices.Marshal]::PtrToStringAuto(
        [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure))
    if ($plain -ne $InstallPwd) {
        Write-Host "  Incorrect password. Setup aborted." -ForegroundColor Red
        return $false
    }
    Write-Host "  Password accepted. Proceeding with install..." -ForegroundColor Green
    return $true
}

function Install-WingetPackage([string]$Id, [string]$Label) {
    if (-not (Test-Winget)) {
        throw "winget is not available. Install '$Label' manually: $Id"
    }
    Write-Info "winget install $Id ..."
    & winget install --id $Id -e --accept-source-agreements --accept-package-agreements --silent
    if ($LASTEXITCODE -gt 1) {
        throw "winget install failed for $Id (exit $LASTEXITCODE)"
    }
}

function Sync-AppSettingsFromEnv {
    $appSettings = Join-Path $EnginePath "appsettings.json"
    if (-not (Test-Path $appSettings)) { return }
    try {
        $json = Get-Content $appSettings -Raw | ConvertFrom-Json
        if ($env:SQL_CONNECTION_STRING) {
            $json.ConnectionStrings.StarSensing = $env:SQL_CONNECTION_STRING
        }
        if ($env:SCAN_INTERVAL_MS) { $json.SensingConfig.ScanIntervalMs = [int]$env:SCAN_INTERVAL_MS }
        if ($env:PYTHON_PROCESSOR_ADDRESS) { $json.SensingConfig.PythonProcessorAddress = $env:PYTHON_PROCESSOR_ADDRESS }
        if ($env:PYTHON_PROCESS_INTERVAL_MS) { $json.SensingConfig.PythonProcessIntervalMs = [int]$env:PYTHON_PROCESS_INTERVAL_MS }
        if ($env:DATA_RETENTION_HOURS) { $json.SensingConfig.DataRetentionHours = [int]$env:DATA_RETENTION_HOURS }
        if ($env:RAW_DATA_RETENTION_HOURS) { $json.SensingConfig.RawDataRetentionHours = [int]$env:RAW_DATA_RETENTION_HOURS }
        if ($env:REPLAY_DATA_RETENTION_HOURS) { $json.SensingConfig.ReplayDataRetentionHours = [int]$env:REPLAY_DATA_RETENTION_HOURS }
        if ($env:ACCESS_POINT_MAX_AGE_DAYS) { $json.SensingConfig.AccessPointMaxAgeDays = [int]$env:ACCESS_POINT_MAX_AGE_DAYS }
        if ($env:MAX_AP_TRACKING_COUNT) { $json.SensingConfig.MaxApTrackingCount = [int]$env:MAX_AP_TRACKING_COUNT }
        if ($env:ENGINE_GRPC_URL) {
            if (-not $json.Kestrel) { $json | Add-Member -NotePropertyName Kestrel -NotePropertyValue (@{}) }
            if (-not $json.Kestrel.Endpoints) { $json.Kestrel | Add-Member -NotePropertyName Endpoints -NotePropertyValue (@{}) }
            if (-not $json.Kestrel.Endpoints.gRPC) { $json.Kestrel.Endpoints | Add-Member -NotePropertyName gRPC -NotePropertyValue (@{}) }
            $json.Kestrel.Endpoints.gRPC.Url = $env:ENGINE_GRPC_URL
        }
        $json | ConvertTo-Json -Depth 10 | Set-Content $appSettings -Encoding UTF8
        Write-Pass "Synced Engine appsettings.json from .env"
    }
    catch {
        Write-Host "  [WARN] Could not sync appsettings.json: $($_.Exception.Message)" -ForegroundColor Yellow
    }
}

function Sync-PythonConfigFromEnv {
    $cfgPath = Join-Path $PyPath "config.yaml"
    $port = if ($env:PYTHON_PORT) { [int]$env:PYTHON_PORT } else { 5051 }
    $models = if ($env:PYTHON_MODELS_DIR) { $env:PYTHON_MODELS_DIR } else { "models" }
    $rate = if ($env:PYTHON_SAMPLE_RATE_HZ) { [double]$env:PYTHON_SAMPLE_RATE_HZ } else { 10.0 }
    $win = if ($env:PYTHON_WINDOW_SIZE) { [int]$env:PYTHON_WINDOW_SIZE } else { 10 }
    $engineUrl = if ($env:ENGINE_GRPC_URL) { $env:ENGINE_GRPC_URL -replace '^https?://', '' } else { "localhost:5050" }

    $yaml = @"
engine_address: "$engineUrl"
server_port: $port
sample_rate_hz: $rate
window_size: $win
models_dir: "$models"
processing:
  window_size: $win
  cutoff_frequency: 2.0
  motion_threshold: 3.0
"@
    Set-Content -Path $cfgPath -Value $yaml -Encoding UTF8
    Write-Pass "Synced Python config.yaml from .env"
}

# ── Register checks ──────────────────────────────────────────────────────────
Write-Head "STAR_SENSING setup preflight"
Write-Info "Root: $Root"
Write-Info "Config: $(Join-Path $Root '.env')"

# OS
if ($env:OS -match "Windows") {
    Write-Pass "Windows OS"
}
else {
    Write-Fail "Windows required (WPF Dashboard + Wi-Fi APIs)"
}

# .NET 8
if (Test-DotNetSdk -Major $EngineSdk) {
    Write-Pass ".NET SDK $EngineSdk.x (Engine)"
}
else {
    Register-Issue ".NET SDK $EngineSdk.x not found (Engine)"
}

# .NET 9
if (Test-DotNetSdk -Major $DashSdk) {
    Write-Pass ".NET SDK $DashSdk.x (Dashboard)"
}
else {
    Register-Issue ".NET SDK $DashSdk.x not found (Dashboard)"
}

# Python
$pyOk, $pyVer = Test-PythonVersion
if ($pyOk) {
    Write-Pass "Python $pyVer"
}
else {
    Register-Issue "Python $PyMinMajor.$PyMinMinor+ not found"
}

# SQL Server
if (Test-SqlServer) {
    $dbName = if ($env:SQL_DATABASE) { $env:SQL_DATABASE } else { "StarSensing" }
    Write-Pass "SQL Server reachable (database $dbName created on first Engine run if missing)"
}
else {
    Register-Issue "Cannot connect to SQL Server - install/start SQL Server and verify SQL_CONNECTION_STRING in .env"
    Write-Info 'Express example: Server=localhost\SQLEXPRESS;Database=StarSensing;Trusted_Connection=True;TrustServerCertificate=True;'
}

# ODBC (Python training)
if (Test-OdbcDriver17) {
    Write-Pass "ODBC Driver 17 for SQL Server"
}
else {
    Register-Issue "ODBC Driver 17 for SQL Server (needed for train_models.py)"
}

# Python venv
if (Test-PythonVenv) {
    Write-Pass "Python venv ($PyVenv)"
}
else {
    Register-Issue "Python virtual environment not found at src/StarSensing.Python/venv"
}

# Python packages
if (Test-PythonDeps) {
    Write-Pass "Python packages (grpc, tensorflow, sklearn, ...)"
}
else {
    Register-Issue "Python venv missing packages - run pip install -r requirements.txt"
}

# ML models
if (Test-MlModels) {
    Write-Pass "ML models in $ModelsDir"
}
else {
    Register-Issue "ML models not trained yet (lstm_motion.*)"
}

# Wi-Fi (warn only)
$wifi = Test-WiFiAdapter
if ($wifi -eq $true) { Write-Pass "Wi-Fi adapter detected" }
elseif ($wifi -eq $false) { Write-Host "  [WARN] No active Wi-Fi adapter - scanning will not work until Wi-Fi is enabled." -ForegroundColor Yellow }
else { Write-Info "Could not verify Wi-Fi adapter (non-fatal)" }

# dotnet restore hint
if ((Test-DotNetSdk -Major $EngineSdk) -and (Test-DotNetSdk -Major $DashSdk)) {
    $sln = Join-Path $Root "StarSensing.slnx"
    if (Test-Path $sln) {
        Write-Pass "Solution file StarSensing.slnx"
    }
    else {
        Write-Fail "StarSensing.slnx not found"
    }
}

# ── Summary ──────────────────────────────────────────────────────────────────
Write-Head "Summary"
if ($script:Issues.Count -eq 0) {
    Write-Host "  All checks passed. Ready to run Start-StarSensing.ps1" -ForegroundColor Green
    Sync-AppSettingsFromEnv
    Sync-PythonConfigFromEnv
    exit 0
}

Write-Host "  $($script:Issues.Count) issue(s) found:" -ForegroundColor Yellow
foreach ($i in $script:Issues) { Write-Host "    - $i" -ForegroundColor Yellow }

if ($CheckOnly) {
    Write-Host ""
    Write-Host "  Run without -CheckOnly to install fixes (password required)." -ForegroundColor Gray
    exit 1
}

if (-not (Request-InstallPassword)) { exit 1 }

if (-not (Test-Admin)) {
    Write-Host "  [WARN] Not running as Administrator - winget/SQL installs may fail." -ForegroundColor Yellow
    Write-Host "  Re-run PowerShell as Admin if installs fail." -ForegroundColor Yellow
}

Write-Head "Applying fixes"
Invoke-ApplyFixes

# Refresh PATH after winget installs
$env:Path = [System.Environment]::GetEnvironmentVariable("Path", "Machine") + ";" +
            [System.Environment]::GetEnvironmentVariable("Path", "User")

Sync-AppSettingsFromEnv
Sync-PythonConfigFromEnv

Write-Head "Re-checking"
$script:Issues.Clear()
& $PSCommandPath -CheckOnly
exit $LASTEXITCODE
