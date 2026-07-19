#!/usr/bin/env python3
"""
Train LSTM motion + CNN heatmap + zone models from SQL (WiFi_Features, Zone_State).

Usage:
  python train_models.py [--connection-string "..."] [--epochs 20]
  python train_models.py --max-rows 0          # all rows (default)

Outputs:
  models/lstm_motion.keras
  models/cnn_heatmap.keras
  models/zone_model.keras
"""

from __future__ import annotations

import argparse
import logging
import os
from pathlib import Path

import numpy as np

from bearing_store import load_bearings_from_sql
from cnn_heatmap import GRID, build_activity_grid
from lstm_predictor import N_FEATURES, SEQ_LEN
from map_geometry import positions_from_signals
from zone_predictor import MAX_ZONES, ZONE_DIM

logging.basicConfig(level=logging.INFO, format="%(asctime)s [%(levelname)s] %(message)s")
logger = logging.getLogger("train_models")

DEFAULT_CS = (
    "Server=localhost;Database=StarSensing;Trusted_Connection=yes;"
    "TrustServerCertificate=yes;Encrypt=no;"
)

FETCH_SIZE = 100_000
LOG_EVERY_ROWS = 500_000

BatchRecord = tuple[str, list[dict], float]


def _pyodbc_connect(connection_string: str):
    import pyodbc  # type: ignore
    cs = connection_string.strip()
    if "DRIVER=" not in cs.upper():
        cs = f"DRIVER={{ODBC Driver 17 for SQL Server}};{cs}"
    return pyodbc.connect(cs)


def _feature_select(max_rows: int) -> str:
    limit = f"TOP ({max_rows}) " if max_rows > 0 else ""
    return f"""
        SELECT {limit}BatchId, Bssid, TimestampMs, RawRssi, Variance, Entropy,
               CrossCorrelation, SpectralEnergy, ChangeRate, MotionConfidence
        FROM dbo.WiFi_Features
        ORDER BY TimestampMs ASC
    """


def _row_to_signal(row: dict) -> dict:
    return {
        "bssid": row["Bssid"],
        "raw_rssi": int(row.get("RawRssi") or -70),
        "variance": float(row["Variance"]),
        "entropy": float(row["Entropy"]),
        "cross_correlation": float(row["CrossCorrelation"]),
        "spectral_energy": float(row["SpectralEnergy"]),
        "change_rate": float(row["ChangeRate"]),
    }


def _load_zone_labels(connection_string: str) -> dict[str, list[dict]]:
    labels: dict[str, list[dict]] = {}
    try:
        conn = _pyodbc_connect(connection_string)
        cur = conn.cursor()
        cur.execute("""
            SELECT BatchId, X, Y, Radius, OccupancyConfidence, MotionConfidence
            FROM dbo.Zone_State
            WHERE BatchId IS NOT NULL
            ORDER BY TimestampMs ASC
        """)
        for row in cur.fetchall():
            bid = str(row[0])
            labels.setdefault(bid, []).append({
                "x": float(row[1]),
                "y": float(row[2]),
                "radius": float(row[3]),
                "occupancy": float(row[4]),
                "motion": float(row[5]),
            })
        conn.close()
        for bid in labels:
            labels[bid] = sorted(labels[bid], key=lambda z: -z["occupancy"])[:MAX_ZONES]
        logger.info("Loaded zone labels for %d batches.", len(labels))
    except Exception as ex:
        logger.warning("Zone label load failed (%s).", ex)
    return labels


def _load_batches(connection_string: str, max_rows: int) -> list[BatchRecord]:
    try:
        import pyodbc  # type: ignore  # noqa: F401
    except ImportError:
        logger.warning("pyodbc not installed — using synthetic training data.")
        return []

    by_batch: dict[str, list[dict]] = {}
    labels: dict[str, float] = {}
    batch_times: dict[str, int] = {}
    row_count = 0

    try:
        conn = _pyodbc_connect(connection_string)
        cur = conn.cursor()
        cur.execute(_feature_select(max_rows))
        cols = [c[0] for c in cur.description]
        while True:
            chunk = cur.fetchmany(FETCH_SIZE)
            if not chunk:
                break
            for raw in chunk:
                row = dict(zip(cols, raw))
                bid = str(row["BatchId"])
                ts = int(row["TimestampMs"])
                by_batch.setdefault(bid, []).append(_row_to_signal(row))
                labels[bid] = max(labels.get(bid, 0.0), float(row.get("MotionConfidence") or 0.0))
                batch_times[bid] = min(batch_times.get(bid, ts), ts)
                row_count += 1
                if row_count % LOG_EVERY_ROWS == 0:
                    logger.info("Loaded %d rows (%d batches)...", row_count, len(by_batch))
        conn.close()

        ordered_ids = sorted(by_batch.keys(), key=lambda k: batch_times[k])
        batches = [(k, by_batch[k], labels[k]) for k in ordered_ids]
        logger.info("Loaded %d feature rows → %d batches from SQL.", row_count, len(batches))
        return batches
    except Exception as ex:
        logger.warning("SQL load failed (%s) — using synthetic data.", ex)
        return []


def _synthetic_batches(n: int = 400) -> list[BatchRecord]:
    batches: list[BatchRecord] = []
    rng = np.random.default_rng(42)
    for i in range(n):
        motion = rng.random() > 0.6
        scale = 4.0 if motion else 0.5
        count = rng.integers(3, 12)
        batch = []
        for j in range(count):
            var = abs(rng.normal(scale, 0.8))
            batch.append({
                "bssid": f"aa:bb:cc:{i:02x}:{j:02x}:00",
                "raw_rssi": int(-40 - rng.integers(10, 50)),
                "variance": float(var),
                "entropy": float(abs(rng.normal(scale * 0.4, 0.2))),
                "cross_correlation": float(rng.uniform(0, 0.8)),
                "spectral_energy": float(abs(rng.normal(scale, 0.5))),
                "change_rate": float(rng.normal(0, scale * 0.3)),
            })
        label = min(1.0, max(s["variance"] for s in batch) / 6.0)
        batches.append((f"synth_{i}", batch, label))
    return batches


def _extract_seq_features(batch: list[dict]) -> np.ndarray:
    variances = [s["variance"] for s in batch]
    entropies = [s["entropy"] for s in batch]
    energies = [s["spectral_energy"] for s in batch]
    correlations = [s["cross_correlation"] for s in batch]
    change_rates = [abs(s["change_rate"]) for s in batch]
    return np.array([
        np.mean(variances), np.max(variances),
        np.mean(entropies), np.max(entropies),
        np.mean(energies), np.max(energies),
        np.mean(correlations), np.mean(change_rates),
    ], dtype=np.float32)


def build_lstm_dataset(batches: list[BatchRecord]):
    xs, ys = [], []
    window: list[np.ndarray] = []
    for _, batch, label in batches:
        feat = _extract_seq_features(batch)
        window.append(feat)
        if len(window) > SEQ_LEN:
            window.pop(0)
        if len(window) >= SEQ_LEN:
            xs.append(np.array(window, dtype=np.float32))
            ys.append(float(label))
    return np.array(xs), np.array(ys, dtype=np.float32)


def build_cnn_dataset(
    batches: list[BatchRecord],
    bearings: dict[str, float],
    map_radius_m: float,
):
    xs, ys = [], []
    for _, batch, label in batches:
        positions = positions_from_signals(batch, bearings, map_radius_m)
        grid = build_activity_grid(batch, positions)
        norm = grid / max(float(grid.max()), 1e-6)
        xs.append(norm)
        ys.append(float(label))
    return np.array(xs, dtype=np.float32), np.array(ys, dtype=np.float32)


def _zones_to_vector(zones: list[dict]) -> np.ndarray:
    out = np.zeros((MAX_ZONES, ZONE_DIM), dtype=np.float32)
    for i, z in enumerate(zones[:MAX_ZONES]):
        out[i] = [
            float(z["x"]), float(z["y"]), float(z["radius"]),
            float(z["occupancy"]), float(z["motion"]),
        ]
    return out.flatten()


def build_zone_dataset(
    batches: list[BatchRecord],
    zone_labels: dict[str, list[dict]],
    bearings: dict[str, float],
    map_radius_m: float,
):
    xs, ys = [], []
    for batch_id, batch, _ in batches:
        zones = zone_labels.get(batch_id)
        if not zones:
            continue
        positions = positions_from_signals(batch, bearings, map_radius_m)
        grid = build_activity_grid(batch, positions)
        norm = grid / max(float(grid.max()), 1e-6)
        xs.append(norm)
        ys.append(_zones_to_vector(zones))
    return np.array(xs, dtype=np.float32), np.array(ys, dtype=np.float32)


def _fit_batch_size(n_samples: int) -> int:
    if n_samples >= 100_000:
        return 256
    if n_samples >= 10_000:
        return 128
    return 32


def train_lstm(x: np.ndarray, y: np.ndarray, epochs: int, out_dir: Path) -> None:
    out_dir.mkdir(parents=True, exist_ok=True)
    batch_size = _fit_batch_size(len(x))
    try:
        from tensorflow import keras  # type: ignore
        from tensorflow.keras import layers  # type: ignore

        model = keras.Sequential([
            layers.Input(shape=(SEQ_LEN, N_FEATURES)),
            layers.LSTM(32, return_sequences=False),
            layers.Dropout(0.2),
            layers.Dense(16, activation="relu"),
            layers.Dense(1, activation="sigmoid"),
        ])
        model.compile(optimizer="adam", loss="mse", metrics=["mae"])
        model.fit(x, y, epochs=epochs, batch_size=batch_size, validation_split=0.15, verbose=1)
        path = out_dir / "lstm_motion.keras"
        model.save(path)
        logger.info("Saved Keras LSTM → %s", path)
        return
    except ImportError:
        logger.info("TensorFlow not available — training sklearn sequence model.")
    except Exception as ex:
        logger.warning("Keras LSTM training failed (%s) — sklearn fallback.", ex)

    import joblib  # type: ignore
    from sklearn.neural_network import MLPRegressor  # type: ignore

    model = MLPRegressor(hidden_layer_sizes=(64, 32), max_iter=300, random_state=42)
    model.fit(x.reshape(len(x), -1), y)
    joblib.dump(model, out_dir / "lstm_motion.joblib")
    logger.info("Saved sklearn sequence model → %s", out_dir / "lstm_motion.joblib")


def train_cnn(x: np.ndarray, y: np.ndarray, epochs: int, out_dir: Path) -> None:
    out_dir.mkdir(parents=True, exist_ok=True)
    batch_size = _fit_batch_size(len(x))
    try:
        from tensorflow import keras  # type: ignore
        from tensorflow.keras import layers  # type: ignore

        x4 = x.reshape(-1, GRID, GRID, 1)
        model = keras.Sequential([
            layers.Input(shape=(GRID, GRID, 1)),
            layers.Conv2D(16, 3, activation="relu", padding="same"),
            layers.MaxPooling2D(2),
            layers.Conv2D(32, 3, activation="relu", padding="same"),
            layers.GlobalAveragePooling2D(),
            layers.Dense(16, activation="relu"),
            layers.Dense(1, activation="sigmoid"),
        ])
        model.compile(optimizer="adam", loss="mse", metrics=["mae"])
        model.fit(x4, y, epochs=epochs, batch_size=batch_size, validation_split=0.15, verbose=1)
        path = out_dir / "cnn_heatmap.keras"
        model.save(path)
        logger.info("Saved Keras CNN → %s", path)
        return
    except ImportError:
        logger.info("TensorFlow not available — training sklearn CNN proxy.")
    except Exception as ex:
        logger.warning("Keras CNN training failed (%s) — sklearn fallback.", ex)

    import joblib  # type: ignore
    from sklearn.neural_network import MLPRegressor  # type: ignore

    model = MLPRegressor(hidden_layer_sizes=(128, 64), max_iter=300, random_state=42)
    model.fit(x.reshape(len(x), -1), y)
    joblib.dump(model, out_dir / "cnn_heatmap.joblib")
    logger.info("Saved sklearn CNN proxy → %s", out_dir / "cnn_heatmap.joblib")


def train_zone(x: np.ndarray, y: np.ndarray, epochs: int, out_dir: Path) -> None:
    if len(x) < 32:
        logger.warning("Only %d zone samples — skipping zone model training.", len(x))
        return

    out_dir.mkdir(parents=True, exist_ok=True)
    batch_size = _fit_batch_size(len(x))
    out_dim = MAX_ZONES * ZONE_DIM
    try:
        from tensorflow import keras  # type: ignore
        from tensorflow.keras import layers  # type: ignore

        x4 = x.reshape(-1, GRID, GRID, 1)
        model = keras.Sequential([
            layers.Input(shape=(GRID, GRID, 1)),
            layers.Conv2D(24, 3, activation="relu", padding="same"),
            layers.MaxPooling2D(2),
            layers.Conv2D(48, 3, activation="relu", padding="same"),
            layers.GlobalAveragePooling2D(),
            layers.Dense(64, activation="relu"),
            layers.Dropout(0.2),
            layers.Dense(out_dim, activation="sigmoid"),
        ])
        model.compile(optimizer="adam", loss="mse", metrics=["mae"])
        model.fit(x4, y, epochs=epochs, batch_size=batch_size, validation_split=0.15, verbose=1)
        path = out_dir / "zone_model.keras"
        model.save(path)
        logger.info("Saved Keras zone model → %s", path)
        return
    except ImportError:
        logger.info("TensorFlow not available — training sklearn zone proxy.")
    except Exception as ex:
        logger.warning("Keras zone training failed (%s) — sklearn fallback.", ex)

    import joblib  # type: ignore
    from sklearn.neural_network import MLPRegressor  # type: ignore

    model = MLPRegressor(hidden_layer_sizes=(256, 128), max_iter=400, random_state=42)
    model.fit(x.reshape(len(x), -1), y)
    joblib.dump(model, out_dir / "zone_model.joblib")
    logger.info("Saved sklearn zone model → %s", out_dir / "zone_model.joblib")


def main() -> None:
    parser = argparse.ArgumentParser(description="Train StarSensing LSTM + CNN + zone models")
    parser.add_argument("--connection-string", default=os.environ.get("STAR_SENSING_CS", DEFAULT_CS))
    parser.add_argument("--epochs", type=int, default=20)
    parser.add_argument("--models-dir", default="models")
    parser.add_argument("--max-rows", type=int, default=0, help="Max WiFi_Features rows (0 = all)")
    parser.add_argument("--map-radius-m", type=float, default=30.0)
    args = parser.parse_args()

    bearings = load_bearings_from_sql(args.connection_string)
    logger.info("Using %d calibrated bearings for map-aligned grids.", len(bearings))

    zone_labels = _load_zone_labels(args.connection_string)
    batches = _load_batches(args.connection_string, args.max_rows)
    if not batches:
        batches = _synthetic_batches()

    if len(batches) < SEQ_LEN + 5:
        logger.error("Not enough batches (%d) to train.", len(batches))
        return

    out_dir = Path(args.models_dir)
    logger.info("Building datasets from %d batches...", len(batches))

    lstm_x, lstm_y = build_lstm_dataset(batches)
    cnn_x, cnn_y = build_cnn_dataset(batches, bearings, args.map_radius_m)
    zone_x, zone_y = build_zone_dataset(batches, zone_labels, bearings, args.map_radius_m)

    logger.info("Training LSTM on %d sequences...", len(lstm_x))
    train_lstm(lstm_x, lstm_y, args.epochs, out_dir)

    logger.info("Training map-aligned CNN on %d heatmaps...", len(cnn_x))
    train_cnn(cnn_x, cnn_y, args.epochs, out_dir)

    logger.info("Training zone model on %d labeled batches...", len(zone_x))
    train_zone(zone_x, zone_y, args.epochs, out_dir)

    logger.info("Done. Restart Python server to load new models.")


if __name__ == "__main__":
    main()
