"""LSTM-style sequence motion predictor (Keras model or sklearn fallback)."""

from __future__ import annotations

import logging
from collections import deque
from pathlib import Path

import numpy as np

logger = logging.getLogger(__name__)

SEQ_LEN = 12
N_FEATURES = 8


def _batch_features(signals: list[dict]) -> np.ndarray | None:
    if not signals:
        return None
    variances = [s.get("variance", 0.0) for s in signals]
    entropies = [s.get("entropy", 0.0) for s in signals]
    energies = [s.get("spectral_energy", 0.0) for s in signals]
    correlations = [s.get("cross_correlation", 0.0) for s in signals]
    change_rates = [abs(s.get("change_rate", 0.0)) for s in signals]
    return np.array([
        float(np.mean(variances)),
        float(np.max(variances)),
        float(np.mean(entropies)),
        float(np.max(entropies)),
        float(np.mean(energies)),
        float(np.max(energies)),
        float(np.mean(correlations)) if correlations else 0.0,
        float(np.mean(change_rates)) if change_rates else 0.0,
    ], dtype=np.float32)


class LstmMotionPredictor:
    """Rolling-window motion prediction using a trained LSTM (or MLP fallback)."""

    def __init__(self, models_dir: str | Path = "models"):
        self.models_dir = Path(models_dir)
        self._buffer: deque[np.ndarray] = deque(maxlen=SEQ_LEN)
        self._keras_model = None
        self._sklearn_model = None
        self._load_models()

    def _load_models(self) -> None:
        joblib_path = self.models_dir / "lstm_motion.joblib"
        from tensorflow import keras  # type: ignore
        for ext in ("keras", "h5"):
            path = self.models_dir / f"lstm_motion.{ext}"
            if not path.exists():
                continue
            try:
                self._keras_model = keras.models.load_model(path, compile=False)
                logger.info("Loaded Keras LSTM from %s", path)
                return
            except Exception as ex:
                logger.warning("Keras LSTM load failed (%s): %s", path.name, ex)

        try:
            if joblib_path.exists():
                import joblib  # type: ignore
                self._sklearn_model = joblib.load(joblib_path)
                logger.info("Loaded sklearn sequence model from %s", joblib_path)
        except Exception as ex:
            logger.warning("Sklearn sequence model load failed: %s", ex)

    @property
    def is_trained(self) -> bool:
        return self._keras_model is not None or self._sklearn_model is not None

    def push(self, signals: list[dict]) -> None:
        feat = _batch_features(signals)
        if feat is not None:
            self._buffer.append(feat)

    def predict(self, signals: list[dict]) -> float:
        self.push(signals)
        if len(self._buffer) < 3:
            return self._heuristic(signals)

        seq = np.array(list(self._buffer), dtype=np.float32)
        if self._keras_model is not None:
            try:
                x = seq.reshape(1, seq.shape[0], seq.shape[1])
                if x.shape[1] < SEQ_LEN:
                    pad = np.zeros((1, SEQ_LEN - x.shape[1], N_FEATURES), dtype=np.float32)
                    x = np.concatenate([pad, x], axis=1)
                elif x.shape[1] > SEQ_LEN:
                    x = x[:, -SEQ_LEN:, :]
                pred = float(self._keras_model(x, training=False).numpy()[0][0])
                return float(np.clip(pred, 0.0, 1.0))
            except Exception as ex:
                logger.debug("Keras predict failed: %s", ex)

        if self._sklearn_model is not None:
            try:
                flat = seq.flatten()
                expected = SEQ_LEN * N_FEATURES
                if flat.size < expected:
                    flat = np.pad(flat, (expected - flat.size, 0))
                elif flat.size > expected:
                    flat = flat[-expected:]
                pred = float(self._sklearn_model.predict(flat.reshape(1, -1))[0])
                return float(np.clip(pred, 0.0, 1.0))
            except Exception as ex:
                logger.debug("Sklearn predict failed: %s", ex)

        return self._heuristic(signals)

    @staticmethod
    def _heuristic(signals: list[dict]) -> float:
        if not signals:
            return 0.0
        max_var = max(s.get("variance", 0.0) for s in signals)
        max_ent = max(s.get("entropy", 0.0) for s in signals)
        from_var = min(1.0, max_var / 6.0)
        from_ent = min(1.0, max_ent / 3.0)
        return float(max(from_var, from_ent * 0.85))
