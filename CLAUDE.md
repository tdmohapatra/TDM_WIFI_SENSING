# STAR_SENSING

Wi-Fi **passive sensing** stack for Windows: scans nearby APs, runs ML on RSSI fluctuations
to detect motion/zones/anomalies (no cameras), stores to SQL Server, visualizes in a WPF dashboard.

**Read `PROJECT_GUIDE.md` first** — canonical file-level map (feature → View/ViewModel/Service,
gRPC endpoints, DB tables, "where do I change X"). This file covers conventions, stack, and
constraints that PROJECT_GUIDE doesn't. Don't re-derive what PROJECT_GUIDE already states.
Ignore `TDM_REFERENCE/ARCHITECTURE.md` (outdated, SQLite era).

## Architecture (data flow, one scan tick)

```
WiFiScannerService (scan ~50ms) → SqlServerStore (raw) → SmartMotionDetector
   → Python gRPC :5051 (ML) or local fallback → SQL (features/motion/zones)
   → SignalAggregator → GrpcSensingService :5050 (stream) → Dashboard tabs (WPF)
```

- **Engine** (.NET 8, ASP.NET Core + Kestrel HTTP/2 :5050): scan loop, persistence, gRPC server.
- **Python** (optional, gRPC :5051): LSTM/CNN motion scores, spatial zones, anomalies.
  Engine falls back to its own `MotionDetectorService` if Python is down.
- **SQL Server** (`StarSensing` DB): single source of truth for all persisted data.
- **Dashboard** (WPF .NET 9): 7 tabs, gRPC client to Engine + direct SQL for locations/replay.

## Tech stack

| Layer | Stack |
|---|---|
| Engine | .NET 8, `Microsoft.NET.Sdk.Worker`, Grpc.AspNetCore 2.80, ManagedNativeWifi 3.0.2, Microsoft.Data.SqlClient 7.0 |
| Dashboard | .NET 9 (`net9.0-windows10.0.19041`), WPF, CommunityToolkit.Mvvm 8.4 (MVVM via `[ObservableProperty]`/source-gen partials), ScottPlot.WPF, SkiaSharp.Views.WPF (3D render) |
| Core | Shared lib (net8.0) — proto source of truth, models, interfaces |
| Python | 3.10+ (3.12 target), gRPC server, TensorFlow-CPU/Keras (`.keras` models — not legacy `.h5`), sklearn fallback (`.joblib`), pyodbc |
| RPC | gRPC + Protobuf (`star_sensing.proto` — single source, regen for C# on Core build, manual regen for Python) |
| DB | SQL Server (local/Express), `StarSensing` database, schema created by Engine on first run |
| Build | `StarSensing.slnx` (3 projects: Core, Engine, Dashboard; Python is separate, no .csproj) |

## Folder structure

```
src/StarSensing.Core/        Protos/, Models/, Interfaces/  — shared, no business logic
src/StarSensing.Engine/      Services/, Workers/            — scan→store→process→publish loop
src/StarSensing.Dashboard/   Views/, ViewModels/, Services/, Models/, Themes/ — WPF, 7 tabs
src/StarSensing.Python/      *.py, models/ (.keras), protos/ — gRPC ML server + offline training
scripts/                     Setup, launch, purge PowerShell scripts + sql/
docs/                        MANUAL_MODEL_TRAINING.md
TDM_REFERENCE/               outdated — ignore
```

## Coding standards & naming conventions

- C#: `Nullable` + `ImplicitUsings` enabled everywhere. File-scoped namespaces.
- Interfaces in `Core/Interfaces` (`IWiFiScanner`, `ISignalStore`, `ISignalProcessor`, `IMotionDetector`)
  define cross-project contracts — implementations live in Engine/Dashboard `Services/`.
- Models are plain sealed classes/DTOs in `Core/Models`, XML-doc commented, `PascalCase` properties
  matching DB column names where applicable (e.g. `Bssid`, `RssiDbm`, `SignalQuality`).
- Dashboard MVVM: `ObservableObject` partial classes + `[ObservableProperty]` (source-generated
  backing fields, `_camelCase` → `PascalCase` property). One View + one ViewModel per tab.
- Services are constructor-injected via DI (Engine `Program.cs`, Dashboard `MainViewModel`).
  `MainViewModel` creates **shared singletons** (GrpcClientService, NetworkFilterManager,
  EnvironmentStreamService, BearingStoreService, CompassService) passed to every tab VM.
- Async suffix `*Async`, cancellation tokens threaded through (`ct = default`).
- Proto messages: `snake_case` fields → generated C# `PascalCase` / Python `snake_case`.
- `star_sensing.proto` is the **only** place to add/change RPCs or messages — never hand-edit generated stubs.

## Build / run / test

```powershell
# Full stack (recommended — preflight checks + setup + build + launch all 3 services)
.\Start-StarSensing.ps1                 # add -SkipBuild / -NoPython / -SkipSetup as needed

# Individual builds
dotnet build src/StarSensing.Core
dotnet build src/StarSensing.Engine
dotnet build src/StarSensing.Dashboard

# Manual run (each in its own terminal)
dotnet run --project src/StarSensing.Engine
.\src\StarSensing.Python\venv\Scripts\python.exe .\src\StarSensing.Python\server.py
dotnet run --project src/StarSensing.Dashboard

# Regenerate Python proto stubs after .proto change
cd src/StarSensing.Python
.\venv\Scripts\python.exe -m grpc_tools.protoc -I../StarSensing.Core/Protos --python_out=protos --grpc_python_out=protos ../StarSensing.Core/Protos/star_sensing.proto
```

**No automated test suite** (no test projects in the solution). Validate changes by running the
stack and checking the relevant Dashboard tab / Engine logs / SQL tables.

Train/retrain ML models: `src/StarSensing.Python/train_models.py` (offline, reads `WiFi_Features`
via pyodbc, writes `.keras` to `models/`) — see `docs/MANUAL_MODEL_TRAINING.md`.

## Deployment notes

This is a **local desktop stack**, not a hosted service — "deployment" = running on a Windows
dev machine via `Start-StarSensing.ps1`. No CI/CD, containers, or cloud infra.

- Config: `.env` (root) → synced by `Setup-StarSensing.ps1` to Engine `appsettings.json` and
  Python `config.yaml`. Never commit real `.env`; use `.env.example` as template.
- Ports: Engine gRPC `5050`, Python gRPC `5051`, SQL Server `1433`.
- `SETUP_INSTALL_PASSWORD` (default `7787`) gates auto-install of missing deps via winget.

## Service interaction map

| Need | Source | Owner |
|---|---|---|
| Live RSSI batches | gRPC `StreamMeasurements` | per-tab ViewModel |
| ML metrics/zones/anomalies | gRPC `StreamEnvironmentState` (single shared stream) | `EnvironmentStreamService` |
| Saved AP bearings (map) | SQL `LocationSignals`/`RouterBearings` | `LocationStoreService` / `BearingStoreService` |
| Motion replay frames | SQL `WiFi_Features` | `SensingDataService` → `MotionViewModel` |
| Save location snapshot | SQL write (Locations + LocationSignals + RouterBearings) | `LocationStoreService` |
| Wi-Fi connect/profiles | local `netsh` (no network calls) | `WifiOperationsService` |

## Database overview (SQL Server `StarSensing`)

Schema auto-created by `SqlServerStoreService.InitializeAsync` (Engine) and
`LocationStoreService.EnsureSchemaAsync` (Dashboard). No migrations framework.

| Table | Owner | Contents |
|---|---|---|
| `Measurements`, `AccessPoints` | Engine | Raw RSSI scans, AP metadata |
| `WiFi_Features` | Engine | Variance/entropy/motion confidence per batch+AP (training source + replay) |
| `Motion_Events`, `Zone_State` | Engine | Detected motion events, spatial zone occupancy |
| `Locations`, `LocationSignals` | Dashboard | Named map snapshots + per-BSSID bearing/distance |
| `RouterBearings`, `MapSettings` | Dashboard | Persistent calibrated bearings, north offset |

Engine purges hourly (`ScanWorker`): separate retention windows for training data, raw data,
replay data, and stale access points — all configured in `appsettings.json` `SensingConfig.*`.

## API overview (gRPC, proto = `Core/Protos/star_sensing.proto`)

- **`SensingService`** (Engine `:5050`, impl `GrpcSensingService`): `StreamMeasurements`,
  `StreamEnvironmentState`, `GetCurrentNetworks`, `GetSavedNetworks`, `TriggerScan`,
  `ResetBaseline`, `GetHistory`.
- **`SignalProcessorService`** (Python `:5051`, impl `server.py`): `ProcessBatch` (used by
  Engine), `StreamProcess` (unused streaming variant).
- ML pipeline inside `ProcessBatch`: `SignalProcessor` → `enrich_signals` + anomaly detection →
  rule-based + LSTM + CNN motion → combined confidence `max(rule, LSTM×0.85, CNN×0.75)` →
  `spatial_inference` → zones.

## Known architectural constraints

- Python integration is **optional/best-effort** — Engine must work standalone via local fallback.
- Several Dashboard tabs each open their **own** `StreamMeasurements` gRPC call (duplicate
  streams to the same Engine — known optimization opportunity, not yet consolidated).
- SQL connection strings are **hardcoded** in Dashboard services (`LocationStoreService`,
  `SensingDataService`) — same string as Engine's `appsettings.json`, kept in sync manually.
- No DI container / ORM on the Dashboard side — services instantiated directly in `MainViewModel`.
- `TDM_REFERENCE/ARCHITECTURE.md` describes an old SQLite-based design — fully superseded by
  SQL Server; do not use it as a reference.

## Important business rules

- **Bearing convention**: compass 0°=N, 90°=E. Resolution priority: `RouterBearings`
  (manual/compass calibration) → location-save snapshot → hash-based estimate.
- **Distance** comes from live RSSI path-loss (`SelectableNetwork.DistanceMeters`); map
  positions must use *live distance + stored bearing*, never compressed normalized coords
  (this caused the "APs clustered at center" bug — see Known Issues).
- **North offset** (`MapSettings.NorthOffsetDeg`) aligns device compass to map north; applied
  before any bearing is persisted or rendered.
- Motion confidence is always the max of three independent signals (rule-based, LSTM, CNN) —
  never trust a single model's score in isolation.
- Data retention is tiered, not uniform: training data, raw scans, and replay data each purge
  on independent windows (`SensingConfig.*RetentionHours`).

## Troubleshooting

| Symptom | Cause / fix |
|---|---|
| Dashboard build fails — DLL locked | Stop the running Dashboard process before rebuilding |
| `NullReferenceException` in `WiFiScannerService` | Overlapping scans return null BSS list — null-safe enum + 2s throttle already applied; if it recurs, check scan interval vs. throttle |
| No ML scores, only fallback values | Python (`:5051`) not running — Engine silently uses `MotionDetectorService` |
| Port 5050/5051 already in use | Stale process; `Start-StarSensing.ps1` runs `Stop-PortListener`, or kill manually |
| Keras model fails to load | Must be `.keras` format — legacy `.h5` is not supported at runtime |
| APs clustered at map center | Bug class: code used normalized coords instead of live distance+bearing |
| SQL connection errors | Check `SQL_CONNECTION_STRING` in `.env`, ensure SQL Server service running, confirm `Trusted_Connection`/`TrustServerCertificate` flags match your instance (Express needs `localhost\SQLEXPRESS`) |
