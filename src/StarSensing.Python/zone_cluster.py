"""KMeans-based activity zone inference from router feature vectors."""

import hashlib
import logging

import numpy as np
from sklearn.cluster import KMeans

logger = logging.getLogger(__name__)

ZONE_COLORS = ["#00f5d4", "#4361ee", "#f59e0b", "#ef4444", "#a855f7", "#43e3a0"]


def _stable_angle(bssid: str) -> float:
    h = hashlib.md5(bssid.upper().encode()).hexdigest()
    return (int(h[:8], 16) % 360) / 360.0


def _stable_radius(bssid: str) -> float:
    h = hashlib.md5(bssid.encode()).hexdigest()
    return 0.15 + (int(h[8:12], 16) % 100) / 200.0


class ZoneClusterer:
    """Clusters routers into spatial activity zones using RSSI feature vectors."""

    def __init__(self, max_zones: int = 4):
        self.max_zones = max_zones

    def infer_zones(
        self,
        processed_signals: list[dict],
        positions: dict[str, tuple[float, float]] | None = None,
    ) -> list[dict]:
        if len(processed_signals) < 2:
            if not processed_signals:
                return []
            s = processed_signals[0]
            x, y = self._position(s["bssid"], positions)
            return [{
                "zone_id": "zone_0",
                "name": "Zone A",
                "x": x,
                "y": y,
                "radius": 0.2,
                "occupancy_confidence": min(1.0, s.get("variance", 0) / 6.0),
                "motion_confidence": min(1.0, s.get("variance", 0) / 6.0),
                "color": ZONE_COLORS[0],
            }]

        n_clusters = min(self.max_zones, len(processed_signals))
        features = []
        meta = []
        for s in processed_signals:
            features.append([
                s.get("variance", 0.0),
                s.get("entropy", 0.0),
                abs(s.get("change_rate", 0.0)),
                s.get("cross_correlation", 0.0),
                self._position(s["bssid"], positions)[0],
            ])
            meta.append(s)

        X = np.array(features, dtype=float)
        try:
            km = KMeans(n_clusters=n_clusters, n_init=10, random_state=42)
            labels = km.fit_predict(X)
        except Exception as ex:
            logger.warning("KMeans failed: %s", ex)
            labels = np.zeros(len(processed_signals), dtype=int)

        zones = []
        for cluster_id in range(n_clusters):
            members = [meta[i] for i, lb in enumerate(labels) if lb == cluster_id]
            if not members:
                continue

            avg_var = float(np.mean([m.get("variance", 0) for m in members]))
            avg_ent = float(np.mean([m.get("entropy", 0) for m in members]))
            motion = min(1.0, avg_var / 6.0)
            occupancy = min(1.0, (avg_var / 6.0) * 0.6 + (avg_ent / 3.0) * 0.4)

            cx = float(np.mean([self._position(m["bssid"], positions)[0] for m in members]))
            cy = float(np.mean([self._position(m["bssid"], positions)[1] for m in members]))
            cy = max(0.1, min(0.9, cy))
            radius = float(np.mean([0.12 + self._position(m["bssid"], positions)[1] * 0.15 for m in members]))

            zones.append({
                "zone_id": f"zone_{cluster_id}",
                "name": f"Zone {chr(65 + cluster_id)}",
                "x": cx,
                "y": cy,
                "radius": radius,
                "occupancy_confidence": occupancy,
                "motion_confidence": motion,
                "color": ZONE_COLORS[cluster_id % len(ZONE_COLORS)],
            })

        return zones

    @staticmethod
    def _position(bssid: str, positions: dict[str, tuple[float, float]] | None) -> tuple[float, float]:
        if positions and bssid in positions:
            return positions[bssid]
        return _stable_angle(bssid), 0.5 + (_stable_radius(bssid) - 0.4)
