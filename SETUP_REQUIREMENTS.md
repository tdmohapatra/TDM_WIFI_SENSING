# STAR_SENSING — Setup Requirements

This document lists everything required to run the **full stack** (Engine + Python ML + Dashboard + SQL Server).

Configuration lives in **`.env`** at the repo root.  
Setup is automated by **`scripts/Setup-StarSensing.ps1`**, which runs automatically when you start the project via **`Start-StarSensing.ps1`**.

---

## Quick start

```powershell
# From repo root (recommended — checks requirements first, then starts everything)
.\Start-StarSensing.ps1

# Check only (no install)
.\scripts\Setup-StarSensing.ps1 -CheckOnly

# Setup only (install missing deps — password required)
.\scripts\Setup-StarSensing.ps1
```

When anything is missing, setup prompts for the **install password** (default: **`7787`**, set in `.env` as `SETUP_INSTALL_PASSWORD`).  
Wrong password → setup aborts; nothing is installed.

---

## System requirements

| Requirement | Version | Purpose |
|-------------|---------|---------|
| **OS** | Windows 10/11 (64-bit) | WPF Dashboard, Wi-Fi APIs, netsh |
| **RAM** | 8 GB min, **16 GB recommended** | Python + TensorFlow + SQL + WPF |
| **Disk** | 20 GB free min, **50 GB+** with SQL + ML | SQL data, Python venv, models |
| **Wi-Fi adapter** | Any supported Windows WLAN | Passive sensing (scan) |
| **Location services** | Enabled (recommended) | Windows Wi-Fi scan API |

---

## Software requirements

| Component | Version | Checked by setup | Auto-install (password `7787`) |
|-----------|---------|------------------|--------------------------------|
| **.NET SDK** | **8.x** | Yes | `winget install Microsoft.DotNet.SDK.8` |
| **.NET SDK** | **9.x** | Yes | `winget install Microsoft.DotNet.SDK.9` |
| **Python** | **3.10+** (3.12 recommended) | Yes | `winget install Python.Python.3.12` |
| **SQL Server** | Express or full, local | Yes (connection test) | Manual — start service / install Express |
| **ODBC Driver 17** | For SQL Server | Yes | `winget install Microsoft.msodbcsql.17` |
| **Python venv** | `src/StarSensing.Python/venv` | Yes | `python -m venv venv` |
| **Python packages** | `requirements.txt` | Yes | `pip install -r requirements.txt` |
| **ML models** | `models/lstm_motion.*` | Yes | `train_models.py` |
| **winget** | Windows Package Manager | Used for installs | Pre-installed on Windows 11 |

---

## Python packages (`requirements.txt`)

```
numpy, scipy, scikit-learn, grpcio, grpcio-tools, pandas, pyyaml, joblib, pyodbc, tensorflow-cpu
```

Install time: **5–15 minutes** (TensorFlow is large).

---

## Ports (configurable in `.env`)

| Service | Default port | Variable |
|---------|--------------|----------|
| Engine gRPC | 5050 | `ENGINE_GRPC_PORT` |
| Python ML gRPC | 5051 | `PYTHON_PORT` |
| SQL Server | 1433 | `SQL_SERVER` |

---

## `.env` configuration reference

| Variable | Default | Description |
|----------|---------|-------------|
| `SETUP_INSTALL_PASSWORD` | `7787` | Password to authorize auto-install |
| `SQL_CONNECTION_STRING` | `Server=localhost;Database=StarSensing;...` | Engine + Dashboard SQL |
| `SQL_SERVER` | `localhost` | SQL host |
| `SQL_DATABASE` | `StarSensing` | Database name |
| `ENGINE_GRPC_PORT` | `5050` | Engine listen port |
| `ENGINE_GRPC_URL` | `http://localhost:5050` | Engine URL (synced to appsettings) |
| `PYTHON_PORT` | `5051` | Python gRPC port |
| `PYTHON_PROCESSOR_ADDRESS` | `http://localhost:5051` | Engine → Python URL |
| `SCAN_INTERVAL_MS` | `50` | Wi-Fi scan interval |
| `DATA_RETENTION_HOURS` | `24` | DB purge window (Engine config) |
| `PYTHON_TRAIN_EPOCHS` | `8` | Initial model training epochs |
| `PYTHON_MODELS_DIR` | `models` | Keras/sklearn model folder |
| `DASHBOARD_GRPC_URL` | `http://localhost:5050` | Dashboard client URL |
| `DOTNET_CONFIGURATION` | `Release` | Build configuration |

Setup **syncs** `.env` → `src/StarSensing.Engine/appsettings.json` and `src/StarSensing.Python/config.yaml` on success.

Copy `.env.example` to `.env` if starting fresh.

---

## What setup checks (in order)

1. Windows OS  
2. .NET 8 SDK (Engine)  
3. .NET 9 SDK (Dashboard)  
4. Python 3.10+  
5. SQL Server connectivity  
6. ODBC Driver 17  
7. Python virtual environment  
8. Python pip packages  
9. Trained LSTM/CNN models  
10. Wi-Fi adapter (warning only)  
11. `StarSensing.slnx` present  

---

## SQL Server notes

- The **Engine creates** database tables on first run if SQL is reachable.
- If you use **SQL Express**, update `.env`:
  ```
  SQL_CONNECTION_STRING=Server=localhost\SQLEXPRESS;Database=StarSensing;Trusted_Connection=True;TrustServerCertificate=True;Encrypt=False;
  ```
- Ensure the Windows service **SQL Server (SQLEXPRESS)** or **MSSQLSERVER** is **Running**.

---

## Manual setup (if auto-install fails)

```powershell
# 1. Install SDKs
winget install Microsoft.DotNet.SDK.8
winget install Microsoft.DotNet.SDK.9
winget install Python.Python.3.12
winget install Microsoft.msodbcsql.17

# 2. Python environment
cd src\StarSensing.Python
python -m venv venv
.\venv\Scripts\pip install -r requirements.txt
.\venv\Scripts\python train_models.py --epochs 8

# 3. Build
dotnet build StarSensing.slnx -c Release

# 4. Start
cd ..\..
.\Start-StarSensing.ps1 -SkipSetup
```

---

## Startup flow

```
Start-StarSensing.ps1
  ├─ Load .env
  ├─ scripts/Setup-StarSensing.ps1  (preflight + optional install)
  ├─ dotnet build
  ├─ Start Python :5051
  ├─ Start Engine :5050
  └─ Start Dashboard (WPF)
```

Skip setup (not recommended): `.\Start-StarSensing.ps1 -SkipSetup`  
Skip Python ML: `.\Start-StarSensing.ps1 -NoPython`  
Skip build: `.\Start-StarSensing.ps1 -SkipBuild`

---

## Troubleshooting

| Problem | Fix |
|---------|-----|
| Setup asks for password | Enter **`7787`** (or value in `.env`) |
| winget install fails | Run PowerShell **as Administrator** |
| SQL connection fails | Start SQL service; fix `SQL_CONNECTION_STRING` |
| Python pip slow/fails | Retry setup; ensure stable internet |
| Dashboard won't build | Install .NET **9** SDK |
| Engine won't build | Install .NET **8** SDK |
| No Wi-Fi data | Enable Wi-Fi + Location services in Windows |

---

## Security note

Change `SETUP_INSTALL_PASSWORD` in `.env` if others can access your machine.  
Do not commit production secrets — use `.env.example` as a template for sharing the repo.

See also: [`PROJECT_GUIDE.md`](PROJECT_GUIDE.md) for architecture and file map.
