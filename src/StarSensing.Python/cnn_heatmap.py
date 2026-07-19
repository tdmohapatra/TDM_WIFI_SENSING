"""CNN spatial heatmap activity analyzer (Keras model or numpy fallback)."""

from __future__ import annotations

import logging
from pathlib import Path

import numpy as np

logger = logging.getLogger(__name__)

GRID = 24


def build_activity_grid(
    processed_signals: list[dict],
    positions: dict[str, tuple[float, float]] | None = None,
) -> np.ndarray:
    """Build a GRID×GRID activity image from router features and optional positions."""
    grid = np.zeros((GRID, GRID), dtype=np.float32)
    if not processed_signals:
        return grid

    for s in processed_signals:
        bssid = s["bssid"]
        if positions and bssid in positions:
            x, y = positions[bssid]
        else:
            h = abs(hash(bssid.upper())) % 10000
            angle = (h % 360) / 360.0
            dist = 0.15 + (h % 100) / 200.0
            x = 0.5 + 0.4 * np.cos(angle * 2 * np.pi)
            y = 0.5 + 0.4 * np.sin(angle * 2 * np.pi) * dist

        col = int(np.clip(x, 0, 0.999) * GRID)
        row = int(np.clip(y, 0, 0.999) * GRID)
        activity = max(s.get("variance", 0.0), s.get("entropy", 0.0) * 2.0)
        grid[row, col] = max(grid[row, col], activity)

    # Gaussian blur spread for visibility
    if grid.max() > 0:
        from scipy.ndimage import gaussian_filter
        grid = gaussian_filter(grid, sigma=0.8)

    return grid


class CnnHeatmapAnalyzer:
    """Classifies spatial activity from a 2D variance heatmap."""

    def __init__(self, models_dir: str | Path = "models"):
        self.models_dir = Path(models_dir)
        self._keras_model = None
        self._sklearn_model = None
        self._load_models()

    def _load_models(self) -> None:
        joblib_path = self.models_dir / "cnn_heatmap.joblib"
        from tensorflow import keras  # type: ignore
        for ext in ("keras", "h5"):
            path = self.models_dir / f"cnn_heatmap.{ext}"
            if not path.exists():
                continue
            try:
                self._keras_model = keras.models.load_model(path, compile=False)
                logger.info("Loaded Keras CNN from %s", path)
                return
            except Exception as ex:
                logger.warning("Keras CNN load failed (%s): %s", path.name, ex)

        try:
            if joblib_path.exists():
                import joblib  # type: ignore
                self._sklearn_model = joblib.load(joblib_path)
                logger.info("Loaded sklearn CNN proxy from %s", joblib_path)
        except Exception as ex:
            logger.warning("Sklearn CNN proxy load failed: %s", ex)

    @property
    def is_trained(self) -> bool:
        return self._keras_model is not None or self._sklearn_model is not None

    def analyze(
        self,
        processed_signals: list[dict],
        positions: dict[str, tuple[float, float]] | None = None,
    ) -> tuple[float, np.ndarray]:
        grid = build_activity_grid(processed_signals, positions)
        norm = grid / max(float(grid.max()), 1e-6)

        if self._keras_model is not None:
            try:
                x = norm.reshape(1, GRID, GRID, 1).astype(np.float32)
                pred = float(self._keras_model(x, training=False).numpy()[0][0])
                return float(np.clip(pred, 0.0, 1.0)), grid
            except Exception as ex:
                logger.debug("Keras CNN predict failed: %s", ex)

        if self._sklearn_model is not None:
            try:
                pred = float(self._sklearn_model.predict(norm.flatten().reshape(1, -1))[0])
                return float(np.clip(pred, 0.0, 1.0)), grid
            except Exception as ex:
                logger.debug("Sklearn CNN predict failed: %s", ex)

        score = float(np.clip(norm.max() * 0.7 + norm.mean() * 0.3, 0.0, 1.0))
        return score, grid
