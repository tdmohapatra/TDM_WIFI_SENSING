# STAR_SENSING — Project Guide

> **For AI agents and developers:** Read this file **first** before searching the codebase for any STAR_SENSING task. It maps features → files, data flows, and common change locations.

Last updated: 2026-06-04 (bearing calibration + Wi-Fi profiles)

---

## 1. What This Project Does

STAR_SENSING is a **Wi-Fi passive sensing** stack for Windows:

1. **Engine** (.NET 8) scans nearby Wi-Fi access points every ~50 ms via Windows APIs.
2. **Python** (optional, port 5051) runs ML signal processing — motion detection, LSTM/CNN scores, spatial zones, anomalies.
3. **SQL Server** stores raw scans, ML features, motion events, zones, and saved map locations.
4. **Dashboard** (WPF .NET 9) visualizes live streams and historical replay across 7 tabs.

---

## 2. Solution Layout

```
STAR_SENSING/
├── Start-StarSensing.ps1          # Build + launch Engine, Python, Dashboard
├── PROJECT_GUIDE.md               # ← this file
├── TDM_REFERENCE/ARCHITECTURE.md  # Older partial doc (SQLite era — mostly outdated)
└── src/
    ├── StarSensing.Core/          # Shared proto, models, interfaces
    ├── StarSensing.Engine/        # Wi-Fi scan, SQL, gRPC server, ML bridge
    ├── StarSensing.Dashboard/     # WPF UI (7 tabs)
    └── StarSensing.Python/        # gRPC ML processor + training scripts
```

| Project | Target | Role |
|---------|--------|------|
| `StarSensing.Core` | net8.0 | Proto source of truth, C# models, interfaces |
| `StarSensing.Engine` | net8.0 | Background scan loop, persistence, gRPC `:5050` |
| `StarSensing.Dashboard` | net9.0-windows | WPF client, direct SQL for locations/replay |
| `StarSensing.Python` | Python 3 + venv | gRPC ML server `:5051`, model training |

---

## 3. Runtime Architecture

```
┌─────────────────────────────────────────────────────────────────────────┐
│  Dashboard (WPF)                                                        │
│  MainWindow → 7 tab ViewModels                                          │
│    ├─ gRPC → Engine :5050 (measurements, env state, history, networks)  │
│    └─ SQL  → localhost/StarSensing (locations, motion replay, zones)    │
└───────────────────────────────┬─────────────────────────────────────────┘
                                │ gRPC HTTP/2
┌───────────────────────────────▼─────────────────────────────────────────┐
│  Engine (ASP.NET Core + gRPC Kestrel :5050)                             │
│  ScanWorker loop:                                                       │
│    WiFiScanner → SqlServerStore → SmartMotionDetector → SignalAggregator│
│  GrpcSensingService streams batches + environment state to clients      │
└───────────────┬─────────────────────────────┬───────────────────────────┘
                │ gRPC :5051                  │ SQL Server
┌───────────────▼──────────────┐   ┌──────────▼──────────────────────────┐
│  Python SignalProcessor      │   │  Database: StarSensing              │
│  server.py                   │   │  Measurements, AccessPoints,        │
│  motion, LSTM, CNN, spatial  │   │  WiFi_Features, Motion_Events,      │
│                              │   │  Zone_State, Locations,             │
│  train_models.py (offline)   │   │  LocationSignals                    │
└──────────────────────────────┘   └─────────────────────────────────────┘
```

### End-to-end data flow (one scan tick)

```
WiFiScannerService.ScanAsync()
  → ScanWorker stores raw batch          → dbo.Measurements, dbo.AccessPoints
  → SmartMotionDetectorService.ProcessAsync()
      → Python ProcessBatch (or local fallback MotionDetectorService)
      → persists ML output               → dbo.WiFi_Features, Motion_Events, Zone_State
      → EnvironmentStateCache.Put()
  → SignalAggregator.PublishBatchAsync()
  → GrpcSensingService streams to Dashboard tabs
```

### Dashboard read paths

| Need | Source | File |
|------|--------|------|
| Live RSSI batches | gRPC `StreamMeasurements` | Each tab's ViewModel |
| ML metrics, zones, anomalies | gRPC `StreamEnvironmentState` | `EnvironmentStreamService` → all tabs |
| Saved AP bearings for map | SQL `LocationSignals` | `LocationStoreService` |
| Motion replay frames | SQL `WiFi_Features` | `SensingDataService` → `MotionViewModel` |
| Zone history | SQL `Zone_State` | `SensingDataService` |
| Save current location snapshot | SQL write | `LocationStoreService` ← `SignalMonitorViewModel` |

---

## 4. Startup

```powershell
# Recommended — runs requirement check + auto-setup (password 7787) then full stack
.\Start-StarSensing.ps1

# Setup / requirements only
.\scripts\Setup-StarSensing.ps1
.\scripts\Setup-StarSensing.ps1 -CheckOnly

# Options
.\Start-StarSensing.ps1 -SkipBuild
.\Start-StarSensing.ps1 -NoPython
.\Start-StarSensing.ps1 -SkipSetup    # skip preflight (not recommended)
```

**Configuration:** `.env` (all ports, SQL, install password) — see **`SETUP_REQUIREMENTS.md`**.  
Setup syncs `.env` → Engine `appsettings.json` and Python `config.yaml`.

| Service | Port | Entry point |
|---------|------|-------------|
| Engine gRPC | **5050** | `src/StarSensing.Engine/Program.cs` |
| Python ML | **5051** | `src/StarSensing.Python/server.py` |
| SQL Server | 1433 (default) | DB name `StarSensing` |

**Manual run:**
```powershell
dotnet run --project src/StarSensing.Engine
.\src\StarSensing.Python\venv\Scripts\python.exe .\src\StarSensing.Python\server.py
dotnet run --project src/StarSensing.Dashboard
```

**Operational notes:**
- Stop running Dashboard before rebuild (DLL lock).
- `Start-StarSensing.ps1` includes `Stop-PortListener` for stale 5050/5051 processes.
- Restart Engine after `WiFiScannerService` changes.

---

## 5. Configuration

### Engine — `src/StarSensing.Engine/appsettings.json`

| Key | Default | Purpose |
|-----|---------|---------|
| `ConnectionStrings:StarSensing` | `localhost;Database=StarSensing` | SQL Server |
| `SensingConfig:ScanIntervalMs` | `50` | Scan loop delay |
| `SensingConfig:PythonProcessorAddress` | `http://localhost:5051` | Python gRPC |
| `SensingConfig:PythonProcessIntervalMs` | `200` | Throttle Python calls |
| `SensingConfig:DataRetentionHours` | `24` | Purge window |
| `SensingConfig:MaxApTrackingCount` | `50` | Max APs tracked in ML |
| `SensingConfig:MotionThresholds.*` | variance thresholds | Local fallback detector |

### Dashboard SQL connections

Hardcoded in services (same connection string as Engine):
- `LocationStoreService.cs`
- `SensingDataService.cs`

### Python — `src/StarSensing.Python/config.yaml`

`sample_rate_hz`, `window_size`, `models_dir`, server port (5051).

### gRPC client default

`GrpcClientService.cs` → `http://localhost:5050`

---

## 6. gRPC API (Proto Source of Truth)

**File:** `src/StarSensing.Core/Protos/star_sensing.proto`

Regenerated C# stubs: `StarSensing.Core` build output. Python stubs: `src/StarSensing.Python/protos/`.

### SensingService (Engine :5050)

| RPC | Purpose | Server implementation |
|-----|---------|----------------------|
| `StreamMeasurements` | Live scan batches | `GrpcSensingService` ← `SignalAggregator` |
| `StreamEnvironmentState` | ML-processed env state | `GrpcSensingService` ← `EnvironmentStateCache` |
| `GetCurrentNetworks` | Snapshot of visible APs | `GrpcSensingService` + scanner cache |
| `GetSavedNetworks` | AP metadata + RSSI stats | `SqlServerStoreService` |
| `TriggerScan` | Force immediate scan | `WiFiScannerService` |
| `ResetBaseline` | Reset motion baseline | `MotionDetectorService` / Python |
| `GetHistory` | Per-BSSID RSSI history | `SqlServerStoreService` |

### SignalProcessorService (Python :5051)

| RPC | Purpose |
|-----|---------|
| `ProcessBatch` | One batch → motion, features, zones, LSTM/CNN |
| `StreamProcess` | Streaming variant (Engine uses `ProcessBatch`) |

---

## 7. Database Tables

**Schema creation:** `SqlServerStoreService.InitializeAsync()` and `LocationStoreService.EnsureSchemaAsync()`

### Engine-managed

| Table | Written by | Contents |
|-------|------------|----------|
| `Measurements` | `SqlServerStoreService.StoreBatchAsync` | Raw RSSI per AP per scan |
| `AccessPoints` | Same | AP metadata, last seen, min/max RSSI |
| `WiFi_Features` | `SqlServerStoreService.StoreFeaturesAsync` | Variance, entropy, motion confidence per batch/AP |
| `Motion_Events` | `StoreMotionEventsAsync` | Detected motion events |
| `Zone_State` | `StoreZoneStateAsync` | Spatial zones (x, y, radius, occupancy) |

### Dashboard-managed

| Table | Written by | Contents |
|-------|------------|----------|
| `Locations` | `LocationStoreService.SaveLocationAsync` | Named location snapshots |
| `LocationSignals` | Same | Per-BSSID `DirectionDeg`, `DistanceMeters` at save time |

**Purge:** Engine hourly purge of data older than 24h (`ScanWorker` → `PurgeOldDataAsync`).

---

## 8. StarSensing.Core

| Path | Purpose |
|------|---------|
| `Protos/star_sensing.proto` | All gRPC messages and services |
| `Interfaces/IWiFiScanner.cs` | Wi-Fi scan abstraction |
| `Interfaces/ISignalStore.cs` | DB read/write contract |
| `Interfaces/ISignalProcessor.cs` | Batch → environment state |
| `Interfaces/IMotionDetector.cs` | Motion analysis contract |
| `Models/ScanBatch.cs` (via SignalMeasurement, WiFiNetwork) | In-memory scan batch |
| `Models/ProcessedSignal.cs` | Per-AP ML metrics |
| `Models/MotionEvent.cs`, `SpatialZone.cs` | Events and zones |
| `Models/WiFiFeatureRecord.cs` | DB feature row shape |

---

## 9. StarSensing.Engine

| Path | Purpose |
|------|---------|
| `Program.cs` | DI wiring, Kestrel :5050 HTTP/2, hosted `ScanWorker` |
| `Workers/ScanWorker.cs` | Main loop: scan → store → process → publish |
| `Services/WiFiScannerService.cs` | Windows Wi-Fi enumeration (`IWiFiScanner`); null-safe BSS cache, 2s full-scan throttle |
| `Services/SqlServerStoreService.cs` | All Engine SQL: schema, measurements, features, history |
| `Services/SmartMotionDetectorService.cs` | Python gRPC client + local fallback; persists ML; fills cache |
| `Services/MotionDetectorService.cs` | Local variance-based motion (fallback when Python down) |
| `Services/GrpcSensingService.cs` | All Dashboard-facing gRPC endpoints |
| `Services/SignalAggregator.cs` | Bounded channel pub/sub for live measurement streams |
| `Services/EnvironmentStateCache.cs` | Latest processed environment state per batch |
| `appsettings.json` | Connection string, scan/ML config |

**Change X here:**

| Task | File |
|------|------|
| Scan speed / interval | `appsettings.json` → `ScanIntervalMs`; `ScanWorker.cs` |
| Wi-Fi scan bugs / AP list | `WiFiScannerService.cs` |
| New DB column / table | `SqlServerStoreService.cs` |
| New gRPC endpoint | `star_sensing.proto` → regenerate → `GrpcSensingService.cs` |
| Python integration / throttling | `SmartMotionDetectorService.cs` |
| Motion fallback logic | `MotionDetectorService.cs` |

---

## 10. StarSensing.Python

| Path | Purpose |
|------|---------|
| `server.py` | gRPC server entry; wires all processors; `_classify()` activity levels |
| `signal_processor.py` | Sliding window smoothing, batch assembly |
| `feature_extractor.py` | Variance, entropy, correlation, `enrich_signals`, `stability_index` |
| `motion_detector.py` | Rule-based motion from signal variance |
| `anomaly_detector.py` | RSSI anomaly flags |
| `spatial_inference.py` | Zone occupancy from multi-AP geometry |
| `lstm_predictor.py` | LSTM motion confidence (`.keras` models in `models/`) |
| `cnn_heatmap.py` | CNN activity score from spatial heatmap |
| `zone_cluster.py` | Zone clustering helpers |
| `train_models.py` | Offline training from `WiFi_Features` via pyodbc |
| `config.yaml` | Sample rate, window, models directory |
| `protos/` | Generated Python gRPC stubs |

**ML pipeline in `server.py` `ProcessBatch`:**
1. `SignalProcessor.process_batch`
2. `enrich_signals` + `anomaly_detector.detect_anomalies`
3. `motion_detector.detect_motion` + `lstm.predict` + `cnn.analyze`
4. Combined motion confidence = max(rule, LSTM×0.85, CNN×0.75)
5. `spatial.infer_spatial` → zones
6. Returns `ProcessingResult` with per-signal metrics + LSTM/CNN scores

---

## 11. StarSensing.Dashboard

### Shell

| Path | Purpose |
|------|---------|
| `Views/MainWindow.xaml` | Tab shell, Sound toggle, connection status bar |
| `ViewModels/MainViewModel.cs` | Creates shared services, wires all tab VMs, connects gRPC |
| `Themes/DarkTheme.xaml` | Neon cyan dark theme resources |

### Shared services (`Services/`)

| File | Purpose |
|------|---------|
| `GrpcClientService.cs` | gRPC channel to Engine :5050 |
| `EnvironmentStreamService.cs` | **Single** shared `StreamEnvironmentState`; updates `NetworkFilterManager` |
| `NetworkFilterManager.cs` | Master AP list, selection, per-AP ML metrics (`UpdateProcessedSignal`) |
| `LocationStoreService.cs` | Save/load locations; bearing snapshot on save |
| `BearingStoreService.cs` | **Persistent per-BSSID bearing** (`RouterBearings` table), north offset, compass convention (0°=N) |
| `CompassService.cs` | Device compass heading for bearing capture (when sensor available) |
| `WifiOperationsService.cs` | `netsh` scan/connect/profiles/password reveal |
| `SensingDataService.cs` | SQL reads for motion replay, zones, events |
| `SoundService.cs` | Audio feedback (Radar tab); toggled from MainWindow |
| `MapImageService.cs` | Map rendering helpers |

### Models

| File | Purpose |
|------|---------|
| `Models/SelectableNetwork.cs` | One AP row: RSSI, selection, ML fields, distance, bearing helpers |

---

## 12. Dashboard Tabs — Features & Files

Tabs defined in `MainWindow.xaml`. Each tab = `View` + `ViewModel`.

### Signal Monitor

| | |
|---|---|
| **View** | `Views/SignalMonitorView.xaml` (+ `.xaml.cs`) |
| **ViewModel** | `ViewModels/SignalMonitorViewModel.cs` |
| **Features** | Live AP list (online/offline pruning), per-AP RSSI charts, history via `GetHistory`, network filter/selection, **Save Location** (writes `Locations` + `LocationSignals`), motion/stability/anomaly summary from env stream |
| **Data** | gRPC `StreamMeasurements`, `GetHistory`, `GetSavedNetworks`; env stream; SQL via `LocationStoreService` on save |

### Heatmap

| | |
|---|---|
| **View** | `Views/HeatmapView.xaml` |
| **ViewModel** | `ViewModels/HeatmapViewModel.cs` |
| **Features** | Channel waterfall (time × channel × RSSI); 32×32 spatial heatmap; AP positions from live `DistanceMeters` + saved `DirectionDeg` |
| **Data** | gRPC measurements + env stream; `LocationStoreService` for bearings |

### Radar

| | |
|---|---|
| **View** | `Views/RadarView.xaml` |
| **ViewModel** | `ViewModels/RadarViewModel.cs` |
| **Features** | Polar radar blips from signal variance; motion-driven sound via `SoundService` |
| **Data** | gRPC measurements + env stream |

### Area Map

| | |
|---|---|
| **View** | `Views/AreaMapView.xaml` (+ `.xaml.cs` — SkiaSharp 3D render, mouse nav) |
| **ViewModel** | `ViewModels/AreaMapViewModel.cs` |
| **Features** | 3D floor map; **exact direction calibration** (Ctrl+drag, slider, compass capture); direction rays (green=calibrated); meter-accurate distance; modes Selected/All/Connected |
| **Data** | gRPC streams; `BearingStoreService`; `CompassService`; refreshes on bearing change |
| **Calibration** | Click AP (calibration mode) or Ctrl+drag on map → `RouterBearings` SQL; North offset slider |

### SolidView

| | |
|---|---|
| **View** | `Views/SolidView.xaml` |
| **ViewModel** | `ViewModels/SolidViewViewModel.cs` |
| **Features** | 3D waveform solids per selected AP; ML summary panel |
| **Data** | gRPC measurements + env stream |

### Motion

| | |
|---|---|
| **View** | `Views/MotionView.xaml` |
| **ViewModel** | `ViewModels/MotionViewModel.cs` |
| **Features** | Live motion confidence, classification, LSTM/CNN scores, stability, zones list; **SQL replay** of historical `WiFi_Features` with play/pause/scrub |
| **Data** | `EnvironmentStreamService` (live); `SensingDataService` (replay/events/zones) |

### Operations (Network Ops)

| | |
|---|---|
| **View** | `Views/NetworkOpsView.xaml` |
| **ViewModel** | `ViewModels/NetworkOpsViewModel.cs` |
| **Features** | **Scan & Connect** tab (signal bars, BSSID, connect); **Saved Profiles & Passwords** tab (`netsh wlan show profile key=clear`); **Network Tools** console; load saved password into connect form |
| **Data** | `WifiOperationsService` — local Windows `netsh` only |

---

## 13. Shared Dashboard Wiring

`MainViewModel` constructor creates **one** instance each of:
- `GrpcClientService`
- `NetworkFilterManager` (shared across tabs)
- `EnvironmentStreamService` (shared env stream)
- `BearingStoreService` (shared bearing calibration)
- `CompassService`

Each tab ViewModel receives the shared instances so AP selection and ML metrics stay in sync.

**Note:** Several tabs also open their own `StreamMeasurements` gRPC call (duplicate streams — known optimization opportunity).

---

## 14. Spatial / Map Positioning Logic

| Concept | Source |
|---------|--------|
| **Distance (radius)** | Live RSSI → path-loss on `SelectableNetwork.DistanceMeters` |
| **Bearing (angle)** | **Compass convention 0°=N, 90°=E.** Priority: `RouterBearings` (manual/compass) → location save → hash estimate |
| **Calibrate bearing** | Area Map: Ctrl+drag AP, slider, or Compass capture → `BearingStoreService` |
| **North offset** | `MapSettings.NorthOffsetDeg` — aligns device compass to map north |
| **Normalized X/Y** | `BearingStoreService.MetersPolarToNormalized()` |
| **Save bearing** | Signal Monitor Save Location also writes `RouterBearings` with source `location` |

**DB tables (Dashboard):** `RouterBearings`, `MapSettings`, `Locations`, `LocationSignals`

**Key files:** `BearingStoreService.cs`, `AreaMapViewModel.cs`, `AreaMapView.xaml.cs`, `SelectableNetwork.cs`

---

## 15. Quick Lookup — "Where Do I Change…?"

| Goal | Go to |
|------|-------|
| Add Dashboard tab | `MainWindow.xaml`, new View/ViewModel, wire in `MainViewModel.cs` |
| Change live scan rate | `appsettings.json` `ScanIntervalMs` |
| Fix Wi-Fi scan crashes | `WiFiScannerService.cs` |
| Add proto field / RPC | `star_sensing.proto` → rebuild Core → Engine + Dashboard + regenerate Python protos |
| Persist new ML metric | `SmartMotionDetectorService.MapFromPython`, `SqlServerStoreService`, proto `EnvironmentStateMsg` |
| Map bearing / direction calibration | `BearingStoreService.cs`, `AreaMapViewModel.cs`, `AreaMapView.xaml.cs` |
| Wi-Fi connect / show password | `WifiOperationsService.cs`, `NetworkOpsViewModel.cs`, `NetworkOpsView.xaml` |
| Motion replay | `SensingDataService.cs`, `MotionViewModel.cs` |
| Train LSTM/CNN | `train_models.py`, models in `StarSensing.Python/models/` |
| Python motion rules | `motion_detector.py`, `server.py` `_classify` |
| Connection string | `appsettings.json` (Engine) + Dashboard SQL services |
| Startup / ports | `Start-StarSensing.ps1`, `Program.cs`, `server.py` |
| UI theme / colors | `Themes/DarkTheme.xaml` |
| Sound alerts | `SoundService.cs`, `RadarViewModel.cs`, MainWindow Sound checkbox |

---

## 16. Known Issues & Tips

| Issue | Cause / fix |
|-------|-------------|
| APs clustered at map center | Must use live `DistanceMeters` + bearing, not compressed normalized coords |
| `NullReferenceException` in WiFiScanner ~line 59 | Overlapping scans return null BSS list — fixed with null-safe enum + 2s throttle |
| Dashboard build DLL locked | Stop running Dashboard process first |
| Python port 5051 in use | Kill stale process; use `Start-StarSensing.ps1` |
| Keras model load fails | Use `.keras` format, not legacy `.h5` |
| Engine works but no ML scores | Python not running → Engine uses `MotionDetectorService` fallback |
| Multiple gRPC measurement streams | Each tab opens own stream; consider consolidating later |

---

## 17. Build

```powershell
dotnet build src/StarSensing.Core
dotnet build src/StarSensing.Engine
dotnet build src/StarSensing.Dashboard
```

Python proto regen (when proto changes):
```powershell
cd src/StarSensing.Python
.\venv\Scripts\python.exe -m grpc_tools.protoc -I../StarSensing.Core/Protos --python_out=protos --grpc_python_out=protos ../StarSensing.Core/Protos/star_sensing.proto
```

---

## 18. Related Docs

- `TDM_REFERENCE/ARCHITECTURE.md` — early architecture notes; **prefer this file** for current SQL Server layout and file map.
