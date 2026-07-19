"""ML zone predictor trained from Zone_State history (map-aligned activity grids)."""

from __future__ import annotations

import logging
from pathlib import Path

import numpy as np

from cnn_heatmap import GRID, build_activity_grid
from zone_cluster import ZONE_COLORS

logger = logging.getLogger(__name__)

MAX_ZONES = 4
ZONE_DIM = 5  # x, y, radius, occupancy, motion


class ZonePredictor:
    """Predicts up to MAX_ZONES spatial zones from an activity heatmap."""

    def __init__(self, models_dir: str | Path = "models"):
        self.models_dir = Path(models_dir)
        self._keras_model = None
        self._sklearn_model = None
        self._load_models()

    def _load_models(self) -> None:
        joblib_path = self.models_dir / "zone_model.joblib"
        try:
            from tensorflow import keras  # type: ignore
            for ext in ("keras", "h5"):
                path = self.models_dir / f"zone_model.{ext}"
                if not path.exists():
                    continue
                try:
                    self._keras_model = keras.models.load_model(path, compile=False)
                    logger.info("Loaded Keras zone model from %s", path)
                    return
                except Exception as ex:
                    logger.warning("Keras zone load failed (%s): %s", path.name, ex)
        except ImportError:
            pass

        try:
            if joblib_path.exists():
                import joblib  # type: ignore
                self._sklearn_model = joblib.load(joblib_path)
                logger.info("Loaded sklearn zone model from %s", joblib_path)
        except Exception as ex:
            logger.warning("Sklearn zone load failed: %s", ex)

    @property
    def is_trained(self) -> bool:
        return self._keras_model is not None or self._sklearn_model is not None

    def _decode(self, flat: np.ndarray) -> list[dict]:
        arr = np.clip(flat.reshape(MAX_ZONES, ZONE_DIM), 0.0, 1.0)
        zones: list[dict] = []
        for i, row in enumerate(arr):
            occ = float(row[3])
            if occ < 0.04:
                continue
            zones.append({
                "zone_id": f"zone_{i}",
                "name": f"Zone {chr(65 + i)}",
                "x": float(row[0]),
                "y": float(row[1]),
                "radius": float(max(0.08, min(0.35, row[2]))),
                "occupancy_confidence": occ,
                "motion_confidence": float(row[4]),
                "color": ZONE_COLORS[i % len(ZONE_COLORS)],
            })
        zones.sort(key=lambda z: z["occupancy_confidence"], reverse=True)
        return zones

    def predict_from_grid(self, grid: np.ndarray) -> list[dict]:
        norm = grid / max(float(grid.max()), 1e-6)
        flat_in = norm.reshape(1, GRID, GRID, 1).astype(np.float32)

        if self._keras_model is not None:
            try:
                out = self._keras_model.predict(flat_in, verbose=0)[0]
                zones = self._decode(np.asarray(out, dtype=np.float32))
                if zones:
                    return zones
            except Exception as ex:
                logger.debug("Keras zone predict failed: %s", ex)

        if self._sklearn_model is not None:
            try:
                out = self._sklearn_model.predict(norm.flatten().reshape(1, -1))[0]
                zones = self._decode(np.asarray(out, dtype=np.float32))
                if zones:
                    return zones
            except Exception as ex:
                logger.debug("Sklearn zone predict failed: %s", ex)

        return []

    def predict(
        self,
        processed_signals: list[dict],
        positions: dict[str, tuple[float, float]] | None = None,
    ) -> list[dict]:
        if not self.is_trained or not processed_signals:
            return []
        grid = build_activity_grid(processed_signals, positions)
        return self.predict_from_grid(grid)
