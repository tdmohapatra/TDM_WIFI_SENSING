from zone_cluster import ZoneClusterer
from zone_predictor import ZonePredictor


class SpatialInferencer:
    def __init__(self, models_dir: str = "models"):
        self._clusterer = ZoneClusterer(max_zones=4)
        self._zone_model = ZonePredictor(models_dir)

    def infer_spatial(self, processed_signals, positions: dict | None = None):
        zones = self._zone_model.predict(processed_signals, positions)
        if not zones:
            zones = self._clusterer.infer_zones(processed_signals, positions)
        elif len(zones) < 2 and len(processed_signals) >= 4:
            # Blend ML primary zone with cluster detail when only one weak zone predicted.
            clustered = self._clusterer.infer_zones(processed_signals, positions)
            if clustered and clustered[0]["occupancy_confidence"] > zones[0]["occupancy_confidence"]:
                zones = clustered

        occupancy = 0.0
        if zones:
            occupancy = max(z["occupancy_confidence"] for z in zones)
        return {
            "occupancy_confidence": float(occupancy),
            "zones": zones,
        }
