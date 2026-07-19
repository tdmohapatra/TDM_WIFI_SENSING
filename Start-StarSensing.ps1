<#
.SYNOPSIS
    Runs setup preflight, then builds and launches the full StarSensing stack.

.PARAMETER SkipBuild
    Skip the dotnet build step.

.PARAMETER SkipSetup
    Skip requirement check (not recommended).

.PARAMETER NoPython
    Do not start the Python signal-processor service.

.EXAMPLE
    .\Start-StarSensing.ps1
    .\Start-StarSensing.ps1 -SkipBuild
#>

param(
    [switch]$SkipBuild,
    [switch]$SkipSetup,
    [switch]$NoPython
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$Root       = $PSScriptRoot
$ScriptsDir = Join-Path $Root "scripts"

# Load .env
$loadEnv = Join-Path $ScriptsDir "Load-DotEnv.ps1"
if (Test-Path $loadEnv) {
    . $loadEnv
    $null = Import-DotEnv -EnvFile (Join-Path $Root ".env")
}

$EnginePath  = Join-Path $Root "src\StarSensing.Engine"
$DashPath    = Join-Path $Root "src\StarSensing.Dashboard"
$PyPath      = Join-Path $Root "src\StarSensing.Python"
$PyVenv      = Join-Path $PyPath "venv\Scripts\python.exe"
$PyServer    = Join-Path $PyPath "server.py"
$SlnPath     = Join-Path $Root "StarSensing.slnx"

$EnginePort  = if ($env:ENGINE_GRPC_PORT) { [int]$env:ENGINE_GRPC_PORT } else { 5050 }
$PyPort      = if ($env:PYTHON_PORT) { [int]$env:PYTHON_PORT } else { 5051 }
$BuildCfg    = if ($env:DOTNET_CONFIGURATION) { $env:DOTNET_CONFIGURATION } else { "Release" }
$TrainEpochs = if ($env:PYTHON_TRAIN_EPOCHS) { [int]$env:PYTHON_TRAIN_EPOCHS } else { 8 }
$ModelsSubdir = if ($env:PYTHON_MODELS_DIR) { $env:PYTHON_MODELS_DIR } else { "models" }
$ModelsDir    = Join-Path $PyPath $ModelsSubdir

function Write-Step([string]$msg) {
    Write-Host ""
    Write-Host "  >> $msg" -ForegroundColor Cyan
}

function Write-OK([string]$msg) {
    Write-Host "  OK: $msg" -ForegroundColor Green
}

function Write-Warn([string]$msg) {
    Write-Host "  WARN: $msg" -ForegroundColor Yellow
}

function Wait-Port {
    param(
        [int]$Port,
        [int]$TimeoutSec = 30,
        [string]$Label = "port $Port"
    )
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    $lastMsg = [DateTime]::MinValue
    while ((Get-Date) -lt $deadline) {
        try {
            $tcp = New-Object System.Net.Sockets.TcpClient
            $tcp.Connect("127.0.0.1", $Port)
            $tcp.Close()
            return $true
        }
        catch { }
        if (((Get-Date) - $lastMsg).TotalSeconds -ge 5) {
            $remaining = [math]::Max(0, [math]::Ceiling(($deadline - (Get-Date)).TotalSeconds))
            Write-Host "  Waiting for $Label ($remaining s left, TensorFlow may load slowly)..." -ForegroundColor DarkGray
            $lastMsg = Get-Date
        }
        Start-Sleep -Milliseconds 400
    }
    return $false
}

function Stop-PortListener {
    param([int]$Port)
    $conns = Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue
    foreach ($c in $conns) {
        if ($c.OwningProcess -and $c.OwningProcess -ne 0) {
            Stop-Process -Id $c.OwningProcess -Force -ErrorAction SilentlyContinue
        }
    }
}

function Start-LauncherWindow {
    param(
        [string]$ScriptPath,
        [string[]]$ScriptArgs = @()
    )
    $quotedScript = """$ScriptPath"""
    $argString = "-NoExit -ExecutionPolicy Bypass -File $quotedScript"
    if ($ScriptArgs.Count -gt 0) {
        $argString += " " + ($ScriptArgs -join " ")
    }
    return Start-Process -FilePath "powershell.exe" -ArgumentList $argString -PassThru
}

# ---------------------------------------------------------------------------
# 0. Setup preflight (check only - run Setup-StarSensing.ps1 to install)
# ---------------------------------------------------------------------------
if (-not $SkipSetup) {
    $setupScript = Join-Path $ScriptsDir "Setup-StarSensing.ps1"
    if (Test-Path $setupScript) {
        Write-Step "Running setup preflight..."
        & $setupScript -CheckOnly
        if ($LASTEXITCODE -ne 0) {
            Write-Host ""
            Write-Host "  Setup check failed. Fix missing items:" -ForegroundColor Red
            Write-Host "    .\scripts\Setup-StarSensing.ps1" -ForegroundColor Yellow
            Write-Host "  (install password is in .env SETUP_INSTALL_PASSWORD, default 7787)" -ForegroundColor Gray
            exit 1
        }
        if (-not $?) {
            Write-Host ""
            Write-Host "  Setup check failed (script error)." -ForegroundColor Red
            exit 1
        }
    }
    else {
        Write-Warn "Setup script not found - skipping preflight."
    }
}

# ---------------------------------------------------------------------------
# 1. Kill leftover processes
# ---------------------------------------------------------------------------
Write-Step "Stopping any previous StarSensing processes..."

foreach ($name in @("StarSensing.Engine", "StarSensing.Dashboard")) {
    Get-Process -Name $name -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
}

Get-Process | Where-Object { $_.MainWindowTitle -like "*STAR_SENSING*" } |
    ForEach-Object { Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue }

Stop-PortListener -Port $EnginePort
Stop-PortListener -Port $PyPort

Start-Sleep -Milliseconds 500
Write-OK "Clean slate."

# ---------------------------------------------------------------------------
# 2. Build
# ---------------------------------------------------------------------------
$didBuild = $false
if (-not $SkipBuild) {
    Write-Step "Building solution ($BuildCfg)..."
    & dotnet build $SlnPath --configuration $BuildCfg --nologo
    if ($LASTEXITCODE -ne 0) {
        Write-Error "Build failed. Fix the errors above and re-run."
        exit 1
    }
    $didBuild = $true
    Write-OK "Build succeeded."
}
else {
    Write-Warn "Skipping build (-SkipBuild)."
}

# ---------------------------------------------------------------------------
# 3. Start Python service
# ---------------------------------------------------------------------------
if (-not $NoPython) {
    if ((Test-Path $PyVenv) -and (Test-Path $PyServer)) {
        Stop-PortListener -Port $PyPort
        Write-Step "Starting Python SignalProcessor on port $PyPort..."

        Start-LauncherWindow -ScriptPath (Join-Path $ScriptsDir "Launch-Python.ps1") | Out-Null

        if (Wait-Port -Port $PyPort -TimeoutSec 45 -Label "Python on port $PyPort") {
            Write-OK "Python service ready."

            $hasModel = (Test-Path (Join-Path $ModelsDir "lstm_motion.keras")) -or
                        (Test-Path (Join-Path $ModelsDir "lstm_motion.h5")) -or
                        (Test-Path (Join-Path $ModelsDir "lstm_motion.joblib"))
            if (-not $hasModel) {
                Write-Step "Training initial LSTM + CNN models..."
                Push-Location $PyPath
                try {
                    & $PyVenv train_models.py --epochs $TrainEpochs
                    if ($LASTEXITCODE -ne 0) { throw "train_models.py failed" }
                }
                finally {
                    Pop-Location
                }
                Write-OK "Model training finished."
            }
        }
        else {
            Write-Warn "Python service did not open port $PyPort in 45 s (Engine will use local fallback)."
            Write-Host "  Check the 'STAR_SENSING Python' console window for errors, or run:" -ForegroundColor Yellow
            Write-Host "    .\scripts\Launch-Python.ps1" -ForegroundColor Yellow
        }
    }
    else {
        Write-Warn "Python venv not found - run .\scripts\Setup-StarSensing.ps1 first."
    }
}
else {
    Write-Warn "Skipping Python service (-NoPython)."
}

# ---------------------------------------------------------------------------
# 4. Start Engine
# ---------------------------------------------------------------------------
Write-Step "Starting Engine on port $EnginePort..."

$engineLaunchArgs = @()
if ($didBuild) { $engineLaunchArgs += "-NoBuild" }

$engineProc = Start-LauncherWindow -ScriptPath (Join-Path $ScriptsDir "Launch-Engine.ps1") `
    -ScriptArgs $engineLaunchArgs

Write-Host "  Waiting up to 30 s for port $EnginePort..." -ForegroundColor DarkGray

if (-not (Wait-Port -Port $EnginePort -TimeoutSec 60 -Label "Engine on port $EnginePort")) {
    Write-Error "Engine did not open port $EnginePort within 60 seconds."
    exit 1
}

Write-OK "Engine ready (PID $($engineProc.Id))."

# ---------------------------------------------------------------------------
# 5. Launch Dashboard
# ---------------------------------------------------------------------------
Write-Step "Launching Dashboard..."

$dashLaunchArgs = @()
if ($didBuild) { $dashLaunchArgs += "-NoBuild" }

Start-LauncherWindow -ScriptPath (Join-Path $ScriptsDir "Launch-Dashboard.ps1") `
    -ScriptArgs $dashLaunchArgs | Out-Null

Write-OK "Dashboard launched."

# ---------------------------------------------------------------------------
# Summary
# ---------------------------------------------------------------------------
Write-Host ""
Write-Host "  ================================================" -ForegroundColor Cyan
Write-Host "  StarSensing stack is running" -ForegroundColor White
Write-Host "  Engine  : http://localhost:$EnginePort  (gRPC)" -ForegroundColor Gray
if (-not $NoPython -and (Test-Path $PyVenv)) {
    Write-Host "  Python  : http://localhost:$PyPort  (gRPC)" -ForegroundColor Gray
}
Write-Host "  Dashboard: WPF window" -ForegroundColor Gray
Write-Host "  Config  : .env + SETUP_REQUIREMENTS.md" -ForegroundColor Gray
Write-Host "  Close the Engine/Python console windows to shut down." -ForegroundColor Gray
Write-Host "  ================================================" -ForegroundColor Cyan
Write-Host ""
