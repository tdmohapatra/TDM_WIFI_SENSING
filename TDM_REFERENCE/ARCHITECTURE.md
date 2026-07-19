# StarSensing — Technical Design & Architecture Reference

> **Last updated:** June 2026  
> **Target audience:** Developers working on any component of this stack.

---

## Table of Contents

1. [System Overview](#1-system-overview)
2. [Repository Layout](#2-repository-layout)
3. [Data Flow — End to End](#3-data-flow--end-to-end)
4. [StarSensing.Core](#4-starsensingcore)
5. [StarSensing.Engine](#5-starsensingengine)
  - 5.1 [WiFiScannerService](#51-wifiscannerservice)
  - 5.2 [ScanWorker](#52-scanworker)
  - 5.3 [SignalAggregator](#53-signalaggregator)
  - 5.4 [DataStoreService (SQLite)](#54-datastoreservice-sqlite)
  - 5.5 [MotionDetectorService](#55-motiondetectorservice)
  - 5.6 [GrpcSensingService](#56-grpcsensingservice)
6. [StarSensing.Python](#6-starsensingpython)
  - 6.1 [SignalProcessor](#61-signalprocessor)
  - 6.2 [AnomalyDetector](#62-anomalydetector)
  - 6.3 [MotionDetector (Python)](#63-motiondetector-python)
  - 6.4 [SpatialInferencer](#64-spatialinferencer)
  - 6.5 [server.py](#65-serverpy)
7. [StarSensing.Dashboard](#7-starsensingdashboard)
  - 7.1 [GrpcClientService](#71-grpcclientservice)
  - 7.2 [MainViewModel](#72-mainviewmodel)
  - 7.3 [SignalMonitorView](#73-signalmonitorview)
  - 7.4 [HeatmapView](#74-heatmapview)
  - 7.5 [RadarView](#75-radarview)
  - 7.6 [MotionView](#76-motionview)
8. [gRPC Contract (proto)](#8-grpc-contract-proto)
9. [Key Design Decisions](#9-key-design-decisions)
10. [Known Limitations & Future Work](#10-known-limitations--future-work)

---

## 1. System Overview

StarSensing uses **passive Wi-Fi RSSI sensing** to detect and classify motion in a room without any cameras or dedicated hardware.  
The physical principle: when a person moves, their body absorbs and reflects 2.4 / 5 GHz radio waves, causing measurable fluctuations in the RSSI values reported by nearby Wi-Fi access points.

```
┌──────────────────────────────────────────────────────────────┐
│                        Windows PC                            │
│                                                              │
│  ┌──────────────┐  ScanBatch   ┌────────────────────────┐   │
│  │ Wi-Fi Radio  │─────────────▶│  StarSensing.Engine    │   │
│  │ (NIC / WLAN) │              │  .NET 9 gRPC Server    │   │
│  └──────────────┘              │  Kestrel HTTP/2 :5050  │   │
│                                └───────────┬────────────┘   │
│                                            │  gRPC stream   │
│                          ┌─────────────────▼──────────────┐ │
│  ┌──────────────────┐    │  StarSensing.Dashboard         │ │
│  │ StarSensing.     │    │  WPF .NET 9 Application        │ │
│  │ Python           │    │  Signal Monitor / Heatmap /    │ │
│  │ gRPC :5051       │    │  Radar / Motion tabs           │ │
│  │ (optional)       │    └────────────────────────────────┘ │
│  └──────────────────┘                                        │
└──────────────────────────────────────────────────────────────┘
```

> **Note:** The Python service is architecturally designed to be called by the Engine for advanced signal processing (FFT, ML), but this integration is not yet wired in the Engine. The Engine currently uses its own `MotionDetectorService` for analysis. The Python service runs independently and is ready for future integration.

---

## 2. Repository Layout

```
STAR_SENSING/
├── src/
│   ├── StarSensing.Core/          # Shared models, interfaces, proto definitions
│   │   ├── Interfaces/            # IWiFiScanner, ISignalStore, IMotionDetector
│   │   ├── Models/                # WiFiNetwork, SignalMeasurement, ScanBatch, ...
│   │   └── Protos/
│   │       └── star_sensing.proto # Single source of truth for all gRPC contracts
│   │
│   ├── StarSensing.Engine/        # .NET 8 background worker + gRPC server
│   │   ├── Services/
│   │   │   ├── WiFiScannerService.cs
│   │   │   ├── SignalAggregator.cs
│   │   │   ├── DataStoreService.cs
│   │   │   ├── MotionDetectorService.cs
│   │   │   └── GrpcSensingService.cs
│   │   ├── Workers/
│   │   │   └── ScanWorker.cs
│   │   └── Program.cs
│   │
│   ├── StarSensing.Dashboard/     # WPF .NET 9 desktop UI
│   │   ├── Services/
│   │   │   └── GrpcClientService.cs
│   │   ├── ViewModels/
│   │   │   ├── MainViewModel.cs
│   │   │   ├── SignalMonitorViewModel.cs
│   │   │   ├── HeatmapViewModel.cs
│   │   │   ├── RadarViewModel.cs
│   │   │   └── MotionViewModel.cs
│   │   ├── Views/
│   │   │   ├── MainWindow.xaml
│   │   │   ├── SignalMonitorView.xaml(.cs)
│   │   │   ├── HeatmapView.xaml(.cs)
│   │   │   ├── RadarView.xaml(.cs)
│   │   │   └── MotionView.xaml(.cs)
│   │   └── Themes/DarkTheme.xaml
│   │
│   └── StarSensing.Python/        # Python gRPC signal processor
│       ├── server.py              # gRPC entry point
│       ├── signal_processor.py    # Butterworth filter + FFT
│       ├── motion_detector.py     # IsolationForest + heuristic
│       ├── anomaly_detector.py    # Z-score per BSSID
│       ├── spatial_inference.py   # Occupancy inference
│       ├── config.yaml            # Port config (default 5051)
│       └── protos/                # Auto-generated pb2 stubs
│
├── TDM_REFERENCE/                 # ← You are here
│   └── ARCHITECTURE.md
│
├── Start-StarSensing.ps1          # One-shot launcher script
└── StarSensing.slnx               # Solution file
```

---

## 3. Data Flow — End to End

```
Every 500 ms (configurable):

  [NIC] ──ScanNetworksAsync()──▶ [WiFiScannerService]
                                        │
                                  ScanBatch created
                                  (list of RSSI readings)
                                        │
               ┌────────────────────────┼────────────────────────┐
               ▼                        ▼                         ▼
      [DataStoreService]       [SignalAggregator]       [MotionDetectorService]
       INSERT to SQLite         publish to Channel        variance analysis
       (time-series store)             │
                                       │  broadcast
                              ┌────────┴────────────────┐
                              ▼                         ▼
                   [StreamMeasurements]      [StreamEnvironmentState]
                    gRPC server-stream        gRPC server-stream
                              │                         │
                              ▼                         ▼
                   [SignalMonitorVM]           [MotionViewModel]
                   [HeatmapViewModel]
                   [RadarViewModel]
                              │                         │
                   [ScottPlot / SkiaSharp charts]   [Arc gauge / timeline]
```

---

## 4. StarSensing.Core

**Project type:** .NET 8 class library  
**Role:** Shared contract layer — referenced by both Engine and Dashboard.

### Models


| Class               | Purpose                                                                                                                                                                                                 |
| ------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `WiFiNetwork`       | Snapshot of a detected AP: SSID, BSSID, RSSI, channel, band, timestamp. Contains static helpers `FrequencyToChannel()` and `FrequencyToBand()`.                                                         |
| `SignalMeasurement` | A single timestamped RSSI sample from one AP. Has optional `SmoothedRssi` and `Variance` fields set by the processor. `TimestampMs` is a computed Unix-epoch-ms property used for fast SQLite indexing. |
| `ScanBatch`         | Groups all `SignalMeasurement` objects from one scan cycle into one unit with a unique `BatchId` (GUID), timestamp, and scan duration.                                                                  |
| `ProcessedSignal`   | Output of the motion detector: includes smoothed RSSI, variance, standard deviation, change rate, dominant frequency, spectral energy, and z-score.                                                     |
| `MotionEvent`       | A detected event (MOVEMENT, ANOMALY, etc.) with confidence score, description, and affected APs.                                                                                                        |
| `EnvironmentState`  | Aggregate state per scan: `MotionConfidence` (0–1), `OccupancyConfidence`, `Classification`, list of `ProcessedSignal`, list of `MotionEvent`.                                                          |
| `SpatialZone`       | Represents a room zone with x/y coordinates, radius, and occupancy confidence (reserved for future spatial mapping).                                                                                    |


### Interfaces


| Interface         | Implemented by                                                                   |
| ----------------- | -------------------------------------------------------------------------------- |
| `IWiFiScanner`    | `WiFiScannerService` — scan the NIC and return a `ScanBatch`                     |
| `ISignalStore`    | `DataStoreService` — persist and query measurements in SQLite                    |
| `IMotionDetector` | `MotionDetectorService` — analyze a `ScanBatch` and return an `EnvironmentState` |


### Proto

`Protos/star_sensing.proto` is the **single source of truth** for all inter-process communication.  
`Grpc.Tools` compiles it into C# classes (`StarSensing.cs`, `StarSensingGrpc.cs`) during build via `GrpcServices="Both"` which generates both client and server stubs.  
The Python equivalent (`protos/star_sensing_pb2*.py`) is generated separately with `grpc_tools.protoc`.

---

## 5. StarSensing.Engine

**Project type:** .NET 8 Worker Service (`Microsoft.NET.Sdk.Worker`)  
**Listens on:** `HTTP/2 port 5050` (no TLS — insecure channel for local use)  
**Requires:** Windows + Location Services enabled (for `ManagedNativeWifi`)

### 5.1 WiFiScannerService

```
IWiFiScanner  ◀──  WiFiScannerService
```

**Working principle:**

1. On construction, calls `NativeWifi.EnumerateInterfaces()` to discover all physical Wi-Fi adapters and stores their GUIDs.
2. `ScanAsync()` is the main entry point:
  - Calls `NativeWifi.ScanNetworksAsync(timeout: 5 s)` which triggers a passive BSS scan on all interfaces simultaneously.
  - After the scan completes, calls `NativeWifi.EnumerateBssNetworks(ifaceId)` on each interface to retrieve the BSS network list — this contains the BSSID, SSID, RSSI (in dBm), link quality (0–100), and frequency (Hz).
  - Maps each BSS entry into a `SignalMeasurement` and appends to the current `ScanBatch`.
3. `FrequencyToChannel()` from `WiFiNetwork` converts the raw frequency (kHz) to a channel number:
  - 2.4 GHz: `(freqMHz - 2412) / 5 + 1` → channels 1–13, channel 14 at 2484 MHz
  - 5 GHz: `(freqMHz - 5170) / 5 + 34` → channels 34+
4. Returns the completed `ScanBatch` including the scan duration.

**Failure modes:**

- No interfaces found → returns an empty batch (NetworkCount = 0), logs a warning.
- Location Services disabled → `EnumerateInterfaces()` throws; service logs an error and becomes unavailable.

---

### 5.2 ScanWorker

```
BackgroundService  ◀──  ScanWorker
```

**Working principle:**

`ScanWorker` is the heartbeat of the entire system. It runs as an `IHostedService` background loop:

```
while (not cancelled):
    batch = WiFiScannerService.ScanAsync()
    if batch.NetworkCount > 0:
        SignalAggregator.PublishBatchAsync(batch)   → notify all gRPC stream subscribers
        DataStoreService.StoreBatchAsync(batch)    → persist to SQLite
        MotionDetectorService.AnalyzeAsync(batch)  → update variance state
    
    if 1 hour since last purge:
        DataStoreService.PurgeOldDataAsync(24h)    → housekeeping
    
    await Task.Delay(scanIntervalMs)               → default 500 ms
```

- The scan interval is read from `appsettings.json` key `SensingConfig:ScanIntervalMs` (default 500 ms).
- `InitializeAsync()` is called once before the loop starts to set up the SQLite schema.
- Any exception in the loop is caught, logged, and the loop continues — the worker never crashes.

---

### 5.3 SignalAggregator

**Working principle:**

Implements a **fan-out pub/sub bus** using `System.Threading.Channels`:

- Internally holds a `Channel<ScanBatch>` with capacity 100, configured as `BoundedChannelFullMode.DropOldest` — if the channel is full (consumer is slow), the oldest batch is silently discarded to prevent memory growth.
- `PublishBatchAsync(batch)` writes to the channel writer and caches the batch as `_latestBatch`.
- `SubscribeAsync(ct)` returns `_channel.Reader.ReadAllAsync(ct)` — each call returns an independent async enumerable, so **multiple gRPC clients can subscribe simultaneously** and each receives every published batch.
- `GetLatestBatch()` is a convenience method for querying the most recent state without subscribing.

> **Important:** `Channel` is single-producer single-consumer in terms of delivery — when two clients call `SubscribeAsync`, they share the *same* underlying channel reader, meaning each batch is delivered to only one consumer. In production, this should be replaced with a true fan-out pattern (e.g., a list of `Channel<T>` per subscriber). Currently the Engine serves one Dashboard client at a time effectively.

---

### 5.4 DataStoreService (SQLite)

**Working principle:**

Uses `Microsoft.Data.Sqlite` to maintain a single `StarSensing.db` file (path from `SensingConfig:DatabasePath`).

**Schema:**

```sql
CREATE TABLE Measurements (
    Id          INTEGER PRIMARY KEY AUTOINCREMENT,
    Bssid       TEXT    NOT NULL,
    Ssid        TEXT    NOT NULL,
    RssiDbm     INTEGER NOT NULL,
    Channel     INTEGER NOT NULL,
    FrequencyKHz INTEGER NOT NULL,
    TimestampMs INTEGER NOT NULL      -- Unix epoch milliseconds
);

CREATE INDEX IDX_Measurements_Bssid_Timestamp ON Measurements(Bssid, TimestampMs);
CREATE INDEX IDX_Measurements_Timestamp       ON Measurements(TimestampMs);
```

**Key operations:**


| Method                                  | Description                                                                                                                                                                      |
| --------------------------------------- | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `InitializeAsync()`                     | Opens the SQLite connection and creates the table + indexes if they don't exist. Called once by `ScanWorker` on startup.                                                         |
| `StoreBatchAsync()`                     | Wraps all inserts for a batch in a single SQLite transaction for performance. Uses a `SemaphoreSlim(1,1)` lock so concurrent writers don't corrupt the single connection.        |
| `GetMeasurementsAsync(bssid, from, to)` | Time-range query for a specific BSSID, results ordered by timestamp ASC. Used by the `GetHistory` gRPC call.                                                                     |
| `GetLatestMeasurementsAsync(maxAge_s)`  | Returns the most recent measurement per BSSID seen within the last N seconds, using a correlated subquery `WHERE TimestampMs = (SELECT MAX(...))`. Used by `GetCurrentNetworks`. |
| `PurgeOldDataAsync(retention)`          | Deletes all rows older than the retention period. Called by `ScanWorker` every hour; default retention is 24 hours.                                                              |


All reads and writes share the same `SemaphoreSlim` lock — SQLite does not support concurrent writes on a single connection.

---

### 5.5 MotionDetectorService

**Working principle:**

This is the **C#-native motion detection** that runs in-process inside the Engine (not the Python ML service).

**Algorithm:**

1. For each BSSID in the incoming batch, maintains a rolling window of the last **10 RSSI values** (`Queue<int>` per BSSID, max size 10).
2. Once the window is full, computes:
  - **Mean** of the window.
  - **Variance** = average of squared deviations from the mean.
  - **Std Dev** = √variance.
3. Aggregates variance across all active APs:
  - `avgVariance = totalVariance / activeAps`
4. Maps variance to motion confidence with threshold logic:

  | avgVariance | MotionConfidence      |
  | ----------- | --------------------- |
  | > 6.0       | 1.0 (High motion)     |
  | > 3.0       | 0.7 (Moderate motion) |
  | > 1.5       | 0.3 (Low motion)      |
  | ≤ 1.5       | 0.0 (Static)          |

5. If `avgVariance > 3.0`, a `MotionEvent` of type `Movement` is created and added to `_recentEvents` (capped at 50 events).
6. Returns a complete `EnvironmentState` with all processed signals, the confidence score, and any events.

**Reset:** `ResetBaselineAsync()` clears all RSSI windows and resets `_motionConfidence` to 0. Used when the environment has changed (e.g., furniture moved).

**Physical basis:** When no one is present, RSSI values from fixed APs are nearly constant (variance ≈ 0–1). When a person moves, multipath interference changes, causing RSSI to fluctuate (variance > 3–6).

---

### 5.6 GrpcSensingService

**Working principle:**

The gRPC layer that exposes the Engine's capabilities to the Dashboard and any other clients.


| RPC                      | Type             | Description                                                                                                                                                                                                                   |
| ------------------------ | ---------------- | ----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `StreamMeasurements`     | Server streaming | Subscribes to `SignalAggregator.SubscribeAsync()`. For each `ScanBatch`, maps `SignalMeasurement` → `SignalMeasurementMsg` and writes to the response stream. Respects `MinIntervalMs` from the request to throttle delivery. |
| `StreamEnvironmentState` | Server streaming | Same subscription; for each batch, calls `MotionDetectorService.AnalyzeAsync()` and maps the result to `EnvironmentStateMsg`, including all `ProcessedSignalMsg` entries.                                                     |
| `GetCurrentNetworks`     | Unary            | Calls `DataStoreService.GetLatestMeasurementsAsync(60s)` and returns a `NetworkListResponse`. Used by Dashboard history loading.                                                                                              |
| `TriggerScan`            | Unary            | Immediately calls `WiFiScannerService.ScanAsync()` outside the normal loop, publishes and stores the result. Returns scan metrics.                                                                                            |
| `ResetBaseline`          | Unary            | Calls `MotionDetectorService.ResetBaselineAsync()`.                                                                                                                                                                           |
| `GetHistory`             | Unary            | Queries SQLite via `DataStoreService.GetMeasurementsAsync(bssid, from, to)` and returns `HistoryResponse`. Used to pre-populate Dashboard charts on connect.                                                                  |


Disconnections are handled by catching `OperationCanceledException` (which gRPC fires when the client disconnects or the cancellation token is cancelled).

---

## 6. StarSensing.Python

**Runtime:** Python 3.x with venv at `src/StarSensing.Python/venv/`  
**Listens on:** gRPC port **5051** (insecure)  
**Dependencies:** `grpcio`, `numpy`, `scipy`, `scikit-learn`, `pandas`

This service implements **advanced signal processing** that would be expensive or impractical in C#. It is designed to be called by the Engine via `SignalProcessorService.ProcessBatch` or `StreamProcess`, but the Engine-to-Python call is **not yet integrated** in the current version.

### 6.1 SignalProcessor

**File:** `signal_processor.py`

**Working principle:**

Maintains a per-BSSID history buffer (`deque(maxlen=10)`) of raw RSSI values.

For each BSSID with at least 4 history points:

1. **Butterworth low-pass filter** (`scipy.signal.butter`, order 4):
  - Cutoff: 2 Hz, sample rate: 2 Hz (one sample per 500 ms scan).
  - `filtfilt()` applies the filter in both directions (zero phase shift) to eliminate movement artefacts while preserving the trend.
  - Output: `smoothed_rssi` — the noise-cleaned RSSI.
2. **Rate of change:** `(latest - oldest) / (window_size / sample_rate)` in dBm/sec.
3. **FFT analysis** (`scipy.fft`):
  - Computes the FFT of the zero-mean RSSI history.
  - `dominant_frequency` = frequency bin with the highest magnitude in the positive spectrum.
  - `spectral_energy` = sum of squared FFT magnitudes in the 0.5–3 Hz band (human motion frequencies).
  - Returns full `fft_magnitudes` and `fft_frequencies` arrays for the Dashboard to visualize if needed.
4. **Statistics:** `mean`, `variance`, `std_dev` using numpy.

**Output per signal:** `{bssid, ssid, raw_rssi, smoothed_rssi, variance, std_dev, change_rate, dominant_frequency, spectral_energy, fft_magnitudes, fft_frequencies}`

---

### 6.2 AnomalyDetector

**File:** `anomaly_detector.py`

**Working principle:**

Performs **per-BSSID z-score anomaly detection** using an exponential moving average (EMA) of mean and standard deviation:

1. Maintains a running `{mean, std, count}` dictionary keyed by BSSID.
2. For each new RSSI reading:
  - `z = (value - mean) / (std + ε)` where ε = 1e-6 prevents division by zero.
  - Updates mean and std using EMA with α = 0.1:
    - `new_mean = 0.9 * old_mean + 0.1 * value`
    - `new_std  = √(0.9 * old_std² + 0.1 * (value - new_mean)²)`
3. Flags `is_anomaly = True` when `|z| > 3.0` (3-sigma rule).

This detects sudden, persistent RSSI shifts that are atypical for a given AP — e.g., someone standing near a specific router.

---

### 6.3 MotionDetector (Python)

**File:** `motion_detector.py`

**Working principle:**

A two-stage detector combining an unsupervised ML model with a rule-based heuristic:

**Feature extraction (per batch):**

```
features = [mean(variances), max(variances), mean(spectral_energies), max(spectral_energies), ap_count]
```

**Stage 1 — IsolationForest:**

- Accumulates features in a rolling baseline buffer (max 100 samples).
- After 20 samples, fits an `IsolationForest(contamination=0.1)` — it learns the "normal" distribution of features during the quiet period at startup.
- For each new batch, `score_samples()` returns an anomaly score. Scores < −0.5 indicate an outlier (motion). Confidence = `min(1.0, |score|)`.

**Stage 2 — Heuristic override:**

- `max_variance > 6.0` → confidence = max(current, 1.0)
- `max_variance > 3.0` → confidence = max(current, 0.7)
- `max_variance > 1.5` → confidence = max(current, 0.3)

The heuristic ensures the system works even before the ML model is trained (< 20 samples = first ~10 seconds).

Events are generated when `confidence > 0.5`.

---

### 6.4 SpatialInferencer

**File:** `spatial_inference.py`

Estimates room occupancy from processed signals. Returns `occupancy_confidence` based on the number and variance of active APs. This module is intentionally simple in the current prototype; it is the placeholder for future triangulation or fingerprinting logic.

---

### 6.5 server.py

The gRPC entry point. Reads `config.yaml` for port (default 5051), starts a `ThreadPoolExecutor(max_workers=10)` gRPC server, and exposes `SignalProcessorService`:


| RPC             | Type                    | Description                                                                                                                                   |
| --------------- | ----------------------- | --------------------------------------------------------------------------------------------------------------------------------------------- |
| `ProcessBatch`  | Unary                   | Runs `SignalProcessor → AnomalyDetector → MotionDetector → SpatialInferencer` pipeline on a `MeasurementBatch`. Returns a `ProcessingResult`. |
| `StreamProcess` | Bidirectional streaming | For each incoming `MeasurementBatch` from the client stream, yields a `ProcessingResult` (calls `ProcessBatch` internally).                   |


---

## 7. StarSensing.Dashboard

**Project type:** WPF, .NET 9, targeting `net9.0-windows10.0.19041`  
**Architecture:** MVVM using `CommunityToolkit.Mvvm`

### 7.1 GrpcClientService

Manages the gRPC channel and client stub:

- `Connect(url)` creates a `GrpcChannel.ForAddress(url)` and instantiates `SensingService.SensingServiceClient`.
- Default URL: `http://localhost:5050` (plain HTTP/2, no TLS).
- Called once in `MainViewModel`'s constructor via the `ConnectCommand`.

---

### 7.2 MainViewModel

The root ViewModel created by `MainWindow.xaml` via XAML `DataContext`:

```xml
<Window.DataContext>
    <viewmodels:MainViewModel/>
</Window.DataContext>
```

- Creates `GrpcClientService`, calls `Connect()`, then instantiates all four child ViewModels passing the service reference.
- Exposes `ConnectionStatus` string bound to the status bar.
- Child VMs: `SignalMonitorVM`, `HeatmapVM`, `RadarVM`, `MotionVM`.
- Each child VM starts its gRPC stream subscription immediately in its constructor.

---

### 7.3 SignalMonitorView

**Tab: Signal Monitor**  
**Rendering:** ScottPlot 5 `WpfPlot` + `DataStreamer`

**ViewModel (`SignalMonitorViewModel`):**

- Subscribes to `StreamMeasurements` (500 ms interval).
- Rebuilds the `Networks` `ObservableCollection` each batch — this drives the right-column network list.
- Fires `OnDataReceived(MeasurementBatch)` event for the code-behind.
- `LoadHistoryAsync()`: calls `GetCurrentNetworks` → for each of the top 20 BSSIDs by RSSI, calls `GetHistory(last 60 s, 100 pts)` and fires synthetic batches to pre-populate the chart.

**Code-behind (`SignalMonitorView.xaml.cs`):**

- Maintains a `Dictionary<string BSSID, DataStreamer>` — one rolling 100-point line series per AP.
- On each batch, adds one RSSI point to each BSSID's streamer.
- After updating, computes the **top 10 BSSIDs by current RSSI** and sets `IsVisible = false` on all others — this prevents the legend from overflowing while still tracking all APs internally.
- Calls `WpfPlot1.Refresh()` to redraw.

---

### 7.4 HeatmapView

**Tab: Heatmap (Spectrum Waterfall)**  
**Rendering:** ScottPlot 5 `Heatmap` with Turbo colormap

**ViewModel (`HeatmapViewModel`):**

- Subscribes to `StreamMeasurements` (1000 ms interval — slower to reduce CPU).
- `LoadHistoryAsync(Action<int channel, double rssi>)`: fetches history for all known networks, sorts all measurements by timestamp, and replays them oldest-first via the callback so the view can replay into the waterfall array.

**Code-behind (`HeatmapView.xaml.cs`):**

- Maintains a `double[14, 60]` 2D array — **14 rows (channels 1–14)** × **60 columns (time steps)**.
- Initialized to −100 dBm (no signal floor).
- On each batch:
  1. Shifts all rows left by one column (drops oldest).
  2. Sets the newest column for each active channel to the **strongest RSSI seen on that channel** in this batch.
  3. Calls `_heatmapPlot.Update()` (signals ScottPlot that `Intensities` array has changed in-place).
  4. Calls `WpfPlot1.Refresh()`.
- **Colormap:** `ScottPlot.Colormaps.Turbo` — low values (−100 dBm) = dark blue/purple, high values (−20 dBm) = red/white.
- **ManualRange:** fixed to `(-100, -20)` so color scale doesn't shift with data.
- **ColorBar** added to the right edge showing dBm scale.
- **Y-axis:** custom ticks via `SetTicks(positions, labels)` showing "Ch 1" … "Ch 14".
- **X-axis:** three labeled ticks: "−60s", "−30s", "Now".

**What you see:** A live scrolling heat-map where each row is a Wi-Fi channel and the color intensity shows how busy that channel is over the last 60 seconds. Channel congestion (overlapping networks on the same channel) is visible as sustained bright rows.

---

### 7.5 RadarView

**Tab: Radar**  
**Rendering:** SkiaSharp `SKElement` — custom painted at ~30 fps

**ViewModel (`RadarViewModel`):**

- Subscribes to `StreamMeasurements` (500 ms interval).
- Fires `OnDataReceived(MeasurementBatch)` event. No additional processing.

**Code-behind (`RadarView.xaml.cs`):**

- A `DispatcherTimer` ticks every 33 ms (~30 fps), increments `_sweepAngle` by 2° per tick, and calls `SkiaCanvas.InvalidateVisual()` to trigger a repaint.
- On each `OnPaintSurface`:

1. **Background:** fills with dark navy (`#0d1117`).
2. **Grid:** 4 concentric circles at 25%, 50%, 75%, 100% of the radar radius. Diagonal crosshair lines. Ring labels showing dBm values (−20 / −40 / −60 / −80).
3. **Sweep:** `SKShader.CreateSweepGradient` creates a comet-tail gradient fading 50° behind the leading edge. A solid bright line marks the current sweep angle.
4. **Blips (APs):**
  - **Angle:** `(uint hash of BSSID) % 360` — deterministic pseudo-random, so the same AP always appears at the same angle.
  - **Distance from center:** `1 - ((RSSI + 100) / 80)` — stronger signal = closer to center (inner rings), weaker = outer edge.
  - **Color:**
    - RSSI > −50 dBm → **cyan** (strong)
    - −50 to −70 dBm → **amber** (medium)
    - < −70 dBm → **red** (weak)
  - **Glow:** Angular distance from `_sweepAngle` is computed. Within 60° behind the sweep, a blurred outer halo (`SKMaskFilter.CreateBlur`) is drawn — the blip "lights up" as the sweep passes over it, simulating a real radar display.
  - **Label:** SSID shown for APs with RSSI > −70 dBm.
5. **YOU marker:** cyan dot at center labeled "YOU".

**Physical limitation:** Standard Wi-Fi adapters provide no angle-of-arrival information. The blip angles are hash-based (consistent but arbitrary) — they represent identity, not physical direction.

---

### 7.6 MotionView

**Tab: Motion & Environment**  
**Rendering:** SkiaSharp arc gauge (left) + ScottPlot timeline (right bottom) + XAML list (right)

**ViewModel (`MotionViewModel`):**

- Subscribes to `StreamEnvironmentState` (500 ms interval).
- Updates observable properties: `MotionConfidence` (0–100), `Classification` (string), `ActiveApCount`.
- Maintains `Events` `ObservableCollection<MotionEventMsg>` (last 100 events, newest first).
- Fires `OnStateReceived(EnvironmentStateMsg)` for the code-behind to update plots.

**Code-behind (`MotionView.xaml.cs`):**

**Left column — Arc Gauge (SkiaSharp):**

- 250° arc spanning from lower-left (145°) to lower-right (395°).
- **Background arc:** dark `#1e2545`, tick marks every 10%, labels every 25%.
- **Progress arc:** filled proportionally to `MotionConfidence`. Color-coded:
  - 0–30% → green (`#22c55e`) — static
  - 30–60% → amber (`#f59e0b`) — low activity
  - 60–80% → orange (`#f97316`) — moderate
  - 80–100% → red (`#ef4444`) — high activity
- Leading edge: white blurred dot at the current confidence angle.
- Center text: large bold percentage number in the arc color + "Motion Confidence" subtitle.

**Left column — Timeline (ScottPlot):**

- `DataStreamer` with 60 points, Y-axis locked to 0–100%.
- Each new `EnvironmentStateMsg` adds one point.
- Shows the last 30 seconds of motion confidence history.

**Right column (XAML bindings):**

- Classification string (e.g., "HIGH ACTIVITY").
- Active AP count.
- Scrollable list of detection events with description and confidence percentage.

---

## 8. gRPC Contract (proto)

**File:** `src/StarSensing.Core/Protos/star_sensing.proto`

### SensingService (Engine → Dashboard)


| RPC                      | Request          | Response                     | Notes                             |
| ------------------------ | ---------------- | ---------------------------- | --------------------------------- |
| `StreamMeasurements`     | `StreamRequest`  | `stream MeasurementBatch`    | Live RSSI batches                 |
| `StreamEnvironmentState` | `StreamRequest`  | `stream EnvironmentStateMsg` | Motion analysis results           |
| `GetCurrentNetworks`     | `Empty`          | `NetworkListResponse`        | Latest RSSI per BSSID (last 60 s) |
| `TriggerScan`            | `Empty`          | `ScanResponse`               | Force an immediate scan           |
| `ResetBaseline`          | `Empty`          | `Empty`                      | Clear motion detection history    |
| `GetHistory`             | `HistoryRequest` | `HistoryResponse`            | Time-range query per BSSID        |


### SignalProcessorService (Python, not yet called by Engine)


| RPC             | Request                   | Response                  | Notes                               |
| --------------- | ------------------------- | ------------------------- | ----------------------------------- |
| `ProcessBatch`  | `MeasurementBatch`        | `ProcessingResult`        | Full pipeline: filter + FFT + ML    |
| `StreamProcess` | `stream MeasurementBatch` | `stream ProcessingResult` | Continuous bidirectional processing |


### Key message types

```
MeasurementBatch
  ├── batch_id (string GUID)
  ├── timestamp (google.protobuf.Timestamp)
  ├── scan_duration_ms (int32)
  └── measurements[] (SignalMeasurementMsg)
       ├── bssid, ssid
       ├── rssi_dbm, signal_quality
       ├── channel, frequency_khz
       └── timestamp

EnvironmentStateMsg
  ├── motion_confidence (double 0–1)
  ├── occupancy_confidence (double 0–1)
  ├── classification (EnvironmentClass enum)
  ├── active_ap_count
  ├── stability_index
  ├── signals[] (ProcessedSignalMsg with variance, FFT, z_score, ...)
  └── active_events[] (MotionEventMsg)

HistoryRequest
  ├── bssid (string)
  ├── from (Timestamp)
  ├── to (Timestamp)
  └── max_points (int32)
```

---

## 9. Key Design Decisions


| Decision                                               | Rationale                                                                                                                                                                                                                         |
| ------------------------------------------------------ | --------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| **HTTP/2 plain text (no TLS)**                         | Both Engine and Dashboard run on the same machine; TLS adds complexity with no security benefit in localhost use.                                                                                                                 |
| `**Channel<ScanBatch>` with DropOldest**               | Prevents unbounded memory growth if a subscriber is slow. The Dashboard UI running at 30 fps can always keep up with 2 Hz data.                                                                                                   |
| **In-process C# motion detector**                      | Allows the Engine to function with zero Python dependency. The Python service provides higher accuracy when available.                                                                                                            |
| **ScottPlot for charts, SkiaSharp for custom drawing** | ScottPlot handles axes, ticks, legends, and data management efficiently. SkiaSharp is used when full 2D canvas control is needed (radar sweep, arc gauge).                                                                        |
| `**net9.0-windows10.0.19041` target**                  | `SkiaSharp.Views.WPF 3.x` only ships native assemblies for `net9.0-windows10.0.19041` and `net10.0`. Using `net8.0-windows` would fall back to the .NETFramework 4.x DLL, which fails to render `SKElement` in a .NET 9 WPF host. |
| **History pre-loading on connect**                     | Charts start empty on a fresh connection. Pre-loading 60 seconds of SQLite history via `GetHistory` makes the UI immediately useful.                                                                                              |
| **BSSID hash → radar angle**                           | True angle-of-arrival requires antenna arrays (e.g., MUSIC/ESPRIT algorithms). A single-antenna laptop has no direction information; a stable hash gives each AP a consistent visual identity.                                    |
| **SQLite with a single connection + SemaphoreSlim**    | Avoids the overhead of a connection pool for a single-writer workload. The 500 ms scan rate is well within SQLite's write throughput.                                                                                             |


---

## 10. Known Limitations & Future Work


| Area                | Limitation                                                                                                     | Future Work                                                                                          |
| ------------------- | -------------------------------------------------------------------------------------------------------------- | ---------------------------------------------------------------------------------------------------- |
| **Engine → Python** | Engine never calls the Python service; `MotionDetectorService` (C#) handles all analysis.                      | Wire `GrpcSensingService` to forward each batch to `localhost:5051` after local analysis.            |
| **Fan-out**         | `SignalAggregator` uses a single `Channel<T>` — only one subscriber can receive each batch.                    | Replace with a list of per-subscriber channels or an event bus.                                      |
| **Radar direction** | Angles are hash-based, not physical.                                                                           | Explore CSI (Channel State Information) extraction from Intel/Realtek adapters for angle estimation. |
| **5 GHz heatmap**   | `HeatmapView` only maps channels 1–14 (2.4 GHz).                                                               | Extend the `_intensities` array and add a separate 5 GHz waterfall.                                  |
| **ML baseline**     | Python `IsolationForest` trains on startup data — if there's motion during startup, the baseline is corrupted. | Add a "calibrate" mode that explicitly captures a quiet baseline for 30 seconds.                     |
| **No reconnect**    | If the Engine restarts, Dashboard shows "Failed to connect" and does not retry.                                | Add exponential-backoff reconnection in `GrpcClientService`.                                         |
| **Spatial mapping** | `SpatialZone` model and `SpatialInferencer` exist but produce trivial output.                                  | Implement RSS fingerprinting or MUSIC-based angle estimation for room-level occupancy maps.          |


## **Memory usage (typical Windows PC)**

These are **estimates** for the full stack started with `Start-StarSensing.ps1`. Actual numbers depend on how many APs you see (you’ve had 300+), whether Python/TensorFlow is running, and SQL Server edition.


| **Component**               | **Typical RAM**     | **Notes**                                                    |
| --------------------------- | ------------------- | ------------------------------------------------------------ |
| **SQL Server**              | **500 MB – 2+ GB**  | Buffer pool grows with use; Express is lighter than full SQL |
| **Engine** (.NET)           | **80 – 200 MB**     | Small in-memory caches (see below)                           |
| **Python + TensorFlow**     | **400 MB – 1.5 GB** | Largest consumer when ML models are loaded                   |
| **Dashboard** (WPF)         | **150 – 500 MB**    | SkiaSharp 3D map, ScottPlot charts, many AP rows             |
| **Total (full stack)**      | **~2 – 4 GB**       | Comfortable minimum **8 GB system RAM** recommended          |
| **Total (`**-NoPython`**)** | **~1 – 2 GB**       | Engine + Dashboard + SQL only                                |


### **Why it stays mostly stable in RAM**

The app uses **bounded** in-memory structures, not unbounded history:

- `EnvironmentStateCache` — keeps at most **200** recent processed states
- `SignalAggregator` — channel capacity **100** batches (drops oldest)
- LSTM — rolling window of **12** feature vectors
- Old SQL rows are **purged every hour** (24h retention) — that limits **disk**, not endless RAM growth from DB reads

### **What can grow slowly over hours/days**


| **Area**                             | **Behavior**                                                                         |
| ------------------------------------ | ------------------------------------------------------------------------------------ |
| **Dashboard** `NetworkFilterManager` | One entry per BSSID ever seen in session; with 300+ APs, UI collections use more RAM |
| **Chart/history buffers**            | Signal Monitor history can accumulate per selected AP                                |
| **Python / TensorFlow**              | Often stable after load; TF may not return all memory to OS                          |
| **SQL Server buffer pool**           | May keep caching hot pages — normal SQL behavior                                     |


So: **RAM usually plateaus after warmup**, unless you leave the Dashboard open for days with hundreds of APs and many tabs streaming.

### **Disk (related — often bigger than RAM)**

With **50 ms scans** and many APs, `Measurements` gets **millions of rows per day**. Auto-purge keeps a **rolling ~24 hours**, so disk use can still be **several GB** steady-state (depends on AP count). That is **disk**, not RAM, but worth planning for on the sensing PC.

---

## **Recommended machine spec (sensing PC)**


| **Use case**               | **RAM**            | **Disk**                |
| -------------------------- | ------------------ | ----------------------- |
| Dev / light use (few APs)  | 8 GB               | 20+ GB free             |
| Your setup (many APs + ML) | **16 GB**          | **50+ GB** free for SQL |
| Long-running 24/7          | 16 GB, SSD for SQL | Monitor DB size         |


Check live usage: Task Manager → look for `StarSensing.Engine`, `StarSensing.Dashboard`, `python`, `sqlservr`.

---

## **Using from iPhone, Android, or another laptop**

### **What the project is today**


| **Piece**          | **Where it runs**                              | **Remote-ready?**                                                    |
| ------------------ | ---------------------------------------------- | -------------------------------------------------------------------- |
| **Wi-Fi scanning** | Only the **Windows PC** with the Wi-Fi adapter | No — phone/laptop cannot scan *your* PC’s airspace remotely          |
| **Dashboard**      | **Windows WPF** desktop app                    | No mobile app; not web                                               |
| **Engine gRPC**    | Port **5050**, binds `ListenAnyIP`             | Partially — network *could* connect, but nothing is built for phones |
| **Python ML**      | Port **5051**, `localhost` from Engine         | Internal only                                                        |
| **SQL**            | `localhost` in Engine + Dashboard              | Local only                                                           |


The Dashboard hardcodes:

GrpcClientService.csLines 12-12

public void Connect(string url = "http://localhost:5050")

So **out of the box, only the same PC** running the Dashboard can view the UI.

---

## **Realistic options for other devices**

### **1. Remote desktop (works today, zero code changes)**

Use **RDP**, **Parsec**, **AnyDesk**, or **Chrome Remote Desktop** on the sensing PC.

- Phone/tablet/other laptop → full Dashboard + map + Operations
- Wi-Fi still scanned on the sensing PC
- Easiest option right now

### **2. Another Windows laptop on the same network (possible with changes)**

The Engine already listens on all interfaces (`ListenAnyIP(5050)`). You would need to:

- Open Windows Firewall for ports **5050** (and maybe **1433** if remote SQL access)
- Change Dashboard to connect to `http://<sensing-pc-ip>:5050` instead of `localhost`
- Still run **Engine + Python + SQL on the sensing PC** — the other laptop is only a **viewer**

**Not implemented today** — would require config/UI changes.

### **3. iPhone / Android native app**

**Not supported today.** Would require a new client (web app, Flutter, etc.) that speaks the **gRPC** `SensingService` API. The proto already defines streams (`StreamMeasurements`, `StreamEnvironmentState`), but there is no mobile client in the repo.

### **4. Run the full stack on each laptop**

Each **Windows laptop** could run its own Engine + Dashboard and scan **its own** Wi-Fi environment. That is separate sensing, not “viewing your home/office sensor from your phone.”

### **5. Phone as Wi-Fi client only**

Your phone can connect to Wi-Fi (Operations tab uses **Windows** `netsh` on the PC only). The phone cannot drive STAR_SENSING unless you build a remote API.

---

## **Architecture summary for multi-device**

[Sensing PC - required]

  Wi-Fi adapter → Engine → Python → SQL

                      ↑

              Dashboard (WPF, local today)

[Phone / other laptop - not built]

  Would need: viewer app → gRPC :5050 on sensing PC

  Cannot: scan Wi-Fi or run WPF Dashboard natively

---

## **Practical recommendation**


| **Goal**                     | **Best approach today**                                                 |
| ---------------------------- | ----------------------------------------------------------------------- |
| View from phone              | Remote desktop to sensing PC                                            |
| View from another Windows PC | RDP, or future: configurable Engine URL + firewall                      |
| Lower RAM                    | Start with `-NoPython` (no LSTM/CNN; rule-based motion only)            |
| Lower disk/RAM pressure      | Shorter DB retention (currently 24h hardcoded in Engine)                |
| True mobile dashboard        | New project: web/mobile gRPC client (Agent mode if you want this built) |


**Bottom line:** Plan **~2–4 GB RAM** for the full stack on the sensing machine; growth over time is **mostly bounded in RAM** but **SQL disk** can be large with many APs. For iPhone/Android/other devices, use **remote desktop** now, or plan a **separate viewer app** that connects to the Engine over the network — that is not in the project yet, and Wi-Fi sensing must always stay on the Windows PC with the adapter.