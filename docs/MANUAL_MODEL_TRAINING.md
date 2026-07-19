# Manual ML Model Training

This guide explains how to **retrain StarSensing models manually** using the latest data in SQL Server, or train **from scratch on the full dataset**.

Training is **offline** (Python script). The live stack (Engine + Python server) must be **stopped or left running** — training only reads SQL and writes files under `models/`. Restart the **Python service** after training so it loads the new weights.

---

## What gets trained

| Model file | Purpose | Runtime consumer |
|------------|---------|-------------------|
| `models/lstm_motion.keras` | Sequence motion confidence (LSTM) | `lstm_predictor.py` → Dashboard LSTM % |
| `models/cnn_heatmap.keras` | Spatial activity on map grid (CNN) | `cnn_heatmap.py` → Dashboard CNN % |
| `models/zone_model.keras` | Predicts up to 4 zones (x, y, radius, occupancy, motion) | `zone_predictor.py` → Area Map / Heatmap zones |

If TensorFlow is unavailable, the script falls back to **sklearn** (`.joblib` files). Production should use **TensorFlow** (`.keras`).

---

## Requirements

### Software

| Component | Version / notes |
|-----------|-----------------|
| **Python** | 3.11+ (project uses 3.12 in setup) |
| **Python venv** | `src/StarSensing.Python/venv` |
| **ODBC Driver 17 for SQL Server** | Required for `pyodbc` SQL reads |
| **SQL Server** | LocalDB or full instance; database **`StarSensing`** |
| **Engine running (historical)** | Data is produced while Engine + Python process live scans |

### Python packages (from `requirements.txt`)

```
numpy scipy scikit-learn grpcio grpcio-tools pandas pyyaml joblib pyodbc tensorflow-cpu
```

Install once:

```powershell
cd "d:\WORK ZONE\MY PROJ\STAR_SENSING\src\StarSensing.Python"
python -m venv venv
.\venv\Scripts\pip install -r requirements.txt
```

### Hardware (practical)

| Dataset size | RAM | Time (20 epochs, CPU) |
|--------------|-----|----------------------|
| &lt; 50k rows | 4 GB+ | ~2–5 min |
| ~600k rows | 8 GB+ | ~15–40 min |
| Millions | 16 GB+ | Use `--epochs 10` first; monitor memory |

GPU is optional; the repo uses `tensorflow-cpu`.

### Minimum data

Training needs **batches**, not raw row count:

- Rows are grouped by **`BatchId`** (one Wi‑Fi scan / ML processing cycle).
- **LSTM** needs at least **`SEQ_LEN + 5` = 17 batches** (sequence length is 12).
- **Zone model** needs batches that exist in both **`WiFi_Features`** and **`Zone_State`** (linked by `BatchId`); at least **32** labeled batches recommended.

If SQL is empty or unreachable, `train_models.py` trains on **synthetic data** (not useful for production).

---

## Data source and pipeline

```
Wi‑Fi scan (Engine)
    → Python ProcessBatch (gRPC :5051)
    → Engine persists ML output to SQL
    → train_models.py reads SQL offline
    → writes models/*.keras
```

You do **not** export CSV manually. All training data comes from SQL tables filled while the system runs.

### Primary training tables

#### `dbo.WiFi_Features` (required — LSTM + CNN + zone inputs)

Written by Engine after each Python ML batch (`SqlServerStoreService.StoreFeaturesAsync`).

| Column | Type | Used in training |
|--------|------|------------------|
| `BatchId` | NVARCHAR(64) | Groups APs into one time step |
| `Bssid` | NVARCHAR(64) | AP identity |
| `TimestampMs` | BIGINT | Ordering |
| `RawRssi` | INT | Distance / grid placement |
| `Variance` | FLOAT | LSTM features, activity grid |
| `Entropy` | FLOAT | LSTM features, activity grid |
| `CrossCorrelation` | FLOAT | LSTM features, activity grid |
| `SpectralEnergy` | FLOAT | LSTM features, activity grid |
| `ChangeRate` | FLOAT | LSTM features, activity grid |
| `MotionConfidence` | FLOAT | **Label** for LSTM/CNN (max per batch) |

Other columns (`SmoothedRssi`, `StdDev`, `ZScore`, …) are stored but not read by `train_models.py` today.

#### `dbo.Zone_State` (required for zone model)

Written after each batch with spatial zones from Python ML.

| Column | Type | Used in training |
|--------|------|------------------|
| `BatchId` | NVARCHAR(64) | Join to `WiFi_Features` |
| `X`, `Y` | FLOAT | Normalized map position (0–1) |
| `Radius` | FLOAT | Zone size |
| `OccupancyConfidence` | FLOAT | Zone label |
| `MotionConfidence` | FLOAT | Zone label |
| `TimestampMs` | BIGINT | Ordering |

Up to **4 zones** per batch are used (`MAX_ZONES = 4`).

### Auxiliary table (recommended — map-aligned CNN/zone grids)

#### `dbo.RouterBearings`

| Column | Purpose |
|--------|---------|
| `Bssid` | AP MAC |
| `BearingDeg` | Calibrated direction (Area Map → Save calibration) |

Training builds **24×24 activity heatmaps** by placing each AP on the map using bearing + RSSI distance. Without bearings, BSSID hash estimates direction (weaker spatial accuracy).

**Calibrate APs on Area Map** before a full retrain for best CNN/zone results.

### Tables **not** used for training

| Table | Role |
|-------|------|
| `Measurements` | Raw RSSI only; purged aggressively |
| `Motion_Events` | Event log / dashboard replay |
| `Environment_Batches` | Batch-level summary for dashboard history |
| `AccessPoints` | AP metadata |

Do **not** delete `WiFi_Features` / `Zone_State` before a full retrain. Purge scripts keep training tables longer than raw scans — see [Data retention](#data-retention-before-training).

---

## Internal training format (what the script builds)

### LSTM

- Input: sequences of **12 batches** × **8 aggregated features** per batch (mean/max variance, entropy, spectral energy, correlation, change rate).
- Label: max `MotionConfidence` in that batch (0–1).

### CNN heatmap

- Input: **24×24** grid per batch (variance splats at AP map positions).
- Label: same batch motion confidence.

### Zone model

- Input: same **24×24** grid.
- Label: flattened vector of up to 4 zones × 5 values `(x, y, radius, occupancy, motion)`.

Map radius default: **30 m** (`--map-radius-m`, should match `config.yaml` / dashboard).

---

## Before you train — check data

Run in **SSMS** or `sqlcmd`:

```sql
USE StarSensing;

SELECT COUNT_BIG(*) AS WiFiFeatureRows FROM dbo.WiFi_Features;
SELECT COUNT(DISTINCT BatchId) AS FeatureBatches FROM dbo.WiFi_Features;

SELECT COUNT_BIG(*) AS ZoneRows FROM dbo.Zone_State;
SELECT COUNT(DISTINCT BatchId) AS ZoneBatches FROM dbo.Zone_State
WHERE BatchId IS NOT NULL;

SELECT COUNT(*) AS CalibratedBearings FROM dbo.RouterBearings;

SELECT MIN(TimestampMs) AS OldestMs, MAX(TimestampMs) AS NewestMs FROM dbo.WiFi_Features;
```

**Healthy minimums for a real retrain:**

- `WiFiFeatureRows` → tens of thousands+ (more is better)
- `FeatureBatches` → **≥ 100** (ideally thousands)
- `ZoneBatches` → **≥ 32** with matching `BatchId` in `WiFi_Features`
- `CalibratedBearings` → at least your main router APs

Collect new data by running the stack with Python enabled:

```powershell
.\Start-StarSensing.ps1
```

Let it run during normal activity (walk around, doors, etc.) for hours or days before a full retrain.

---

## Commands

All commands assume repo root unless noted.

### 1. Stop Python only (optional, recommended)

Training does not require stopping Engine/SQL, but restarting Python after training is required. Easiest: close the **STAR_SENSING Python** console window, or stop the process on port **5051**.

### 2. Activate venv and go to Python project

```powershell
cd "d:\WORK ZONE\MY PROJ\STAR_SENSING\src\StarSensing.Python"
.\venv\Scripts\Activate.ps1
```

### 3. Retrain on **all** SQL data (full retrain — default)

```powershell
python train_models.py --epochs 20 --max-rows 0 --models-dir models
```

- `--max-rows 0` → **no row limit** (entire `WiFi_Features` table, ordered by time).
- Overwrites existing `models/*.keras`.

### 4. Retrain on **latest N rows** (quick refresh)

Use when you added recent data and want a faster pass:

```powershell
# Last ~100k feature rows only
python train_models.py --epochs 15 --max-rows 100000 --models-dir models
```

Rows are the **oldest-first** subset when `max-rows` is set (SQL `TOP (N) … ORDER BY TimestampMs ASC`). For strictly *newest* data only, increase retention and run full train, or purge old rows first (see below).

### 5. Custom SQL connection

Default:

```
Server=localhost;Database=StarSensing;Trusted_Connection=yes;TrustServerCertificate=yes;Encrypt=no;
```

Override via flag or environment variable:

```powershell
$env:STAR_SENSING_CS = "Server=localhost;Database=StarSensing;Trusted_Connection=yes;TrustServerCertificate=yes;Encrypt=no;"
python train_models.py --connection-string $env:STAR_SENSING_CS --epochs 20
```

Remote SQL example:

```powershell
python train_models.py --connection-string "Server=MYPC\SQLEXPRESS;Database=StarSensing;Trusted_Connection=yes;TrustServerCertificate=yes;Encrypt=no;"
```

### 6. Full parameter reference

```powershell
python train_models.py `
  --connection-string "Server=localhost;Database=StarSensing;..." `
  --epochs 20 `
  --models-dir models `
  --max-rows 0 `
  --map-radius-m 30.0
```

| Flag | Default | Meaning |
|------|---------|---------|
| `--connection-string` | `STAR_SENSING_CS` env or localhost | SQL Server |
| `--epochs` | `20` | Training epochs per model |
| `--models-dir` | `models` | Output directory |
| `--max-rows` | `0` | Max `WiFi_Features` rows (`0` = all) |
| `--map-radius-m` | `30.0` | Map scale for CNN/zone grids |

### 7. Verify outputs

```powershell
Get-ChildItem models\*.keras, models\*.joblib
```

Expected after successful TensorFlow train:

```
models/lstm_motion.keras
models/cnn_heatmap.keras
models/zone_model.keras
```

### 8. Load new models

Restart Python (full stack):

```powershell
cd "d:\WORK ZONE\MY PROJ\STAR_SENSING"
.\Start-StarSensing.ps1 -SkipBuild
```

Or only Python:

```powershell
.\scripts\Launch-Python.ps1
```

The server reads `models_dir` from `config.yaml` (`models`).

---

## Scenarios

### A. First-time training (no models yet)

`Start-StarSensing.ps1` auto-runs training if `lstm_motion.keras` is missing (8 epochs by default). For production quality, run manually with more epochs after collecting real data:

```powershell
cd src\StarSensing.Python
.\venv\Scripts\python train_models.py --epochs 25 --max-rows 0
```

### B. Regular refresh (new data accumulated)

1. Run stack for several days with motion in the environment.
2. Check row counts (SQL above).
3. Full retrain:

```powershell
.\venv\Scripts\python train_models.py --epochs 20 --max-rows 0
```

4. Restart Python.

### C. Train completely new models from scratch

1. **Optional:** delete old weights:

```powershell
Remove-Item src\StarSensing.Python\models\*.keras, src\StarSensing.Python\models\*.joblib -ErrorAction SilentlyContinue
```

2. Ensure SQL has enough history (do **not** purge training tables).
3. Calibrate bearings on Area Map.
4. Train all data:

```powershell
cd src\StarSensing.Python
.\venv\Scripts\python train_models.py --epochs 25 --max-rows 0
```

5. Restart Python and validate Dashboard (Motion %, CNN %, zones on Area Map / Heatmap).

### D. Train via setup script

```powershell
.\scripts\Setup-StarSensing.ps1
```

Runs `train_models.py` with configurable epochs when models are missing.

---

## Data retention before training

Training data lives in `WiFi_Features` and `Zone_State`. Purge scripts **can delete old training rows** if retention is too short.

Preview purge impact:

```powershell
.\scripts\Invoke-DataPurge.ps1 -Preview
```

Default training retention is often **7–14 days** (`TrainingRetentionHours` in purge SQL). For a **full historical retrain**, extend retention or purge only non-training tables before deleting features.

Tables **never** time-purged by default: `RouterBearings` (calibration).

---

## Troubleshooting

| Symptom | Cause | Fix |
|---------|-------|-----|
| `pyodbc not installed — using synthetic` | Missing ODBC / pyodbc | `pip install pyodbc`; install ODBC Driver 17 |
| `SQL load failed — using synthetic data` | SQL down, wrong DB, auth | Fix connection string; start SQL; check `StarSensing` exists |
| `Not enough batches (N) to train` | &lt; 17 batches in SQL | Run Engine longer; check `WiFi_Features` |
| `Only N zone samples — skipping zone model` | No `Zone_State` with `BatchId` | Run with Python ML; verify zones in dashboard |
| `Loaded 0 calibrated bearings` | No `RouterBearings` | Calibrate on Area Map; save bearings |
| Training fast but models useless | Trained on synthetic data | Fix SQL connection; verify row counts |
| Dashboard still shows old behavior | Python not restarted | Restart Python service after train |
| Out of memory on huge DB | Full table in RAM | Train in chunks with `--max-rows 500000` multiple times*, or add RAM |

\*Chunked training overwrites the same model files each run — for very large DBs prefer one full run on a machine with enough RAM, or temporarily export a date range (advanced; not built-in).

---

## Log output (success)

You should see lines similar to:

```
Loaded 639000 feature rows → 8420 batches from SQL.
Loaded zone labels for 8100 batches.
Using 12 calibrated bearings for map-aligned grids.
Training LSTM on 8408 sequences...
Training map-aligned CNN on 8420 heatmaps...
Training zone model on 8050 labeled batches...
Saved Keras LSTM → models/lstm_motion.keras
Saved Keras CNN → models/cnn_heatmap.keras
Saved Keras zone model → models/zone_model.keras
Done. Restart Python server to load new models.
```

---

## Related files

| Path | Role |
|------|------|
| `src/StarSensing.Python/train_models.py` | Training entry point |
| `src/StarSensing.Python/config.yaml` | `models_dir`, sample rate |
| `src/StarSensing.Python/server.py` | Loads models at runtime |
| `src/StarSensing.Engine/Services/SqlServerStoreService.cs` | Writes `WiFi_Features`, `Zone_State` |
| `scripts/sql/PurgeNonTrainingData.sql` | Retention / purge rules |
| `Start-StarSensing.ps1` | Auto-train on first run if models missing |

---

## Quick reference (copy-paste)

```powershell
# Full retrain on all SQL data
cd "d:\WORK ZONE\MY PROJ\STAR_SENSING\src\StarSensing.Python"
.\venv\Scripts\Activate.ps1
python train_models.py --epochs 20 --max-rows 0 --models-dir models

# Restart stack
cd ..\..
.\Start-StarSensing.ps1 -SkipBuild
```
