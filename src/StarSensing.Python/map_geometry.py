"""Shared map geometry: bearings, RSSI distance, normalized floor-plan coordinates."""

from __future__ import annotations

import math

DEFAULT_MAP_RADIUS_M = 30.0
TX_POWER_DBM = -40.0
PATH_LOSS_N = 2.7


def stable_hash_bearing(bssid: str) -> float:
    """Match dashboard SelectableNetwork.EstimateBearingFromBssid (FNV-1a)."""
    fnv_offset = 2166136261
    fnv_prime = 16777619
    h = fnv_offset
    for c in bssid:
        h ^= ord(c.upper())
        h = (h * fnv_prime) & 0xFFFFFFFF
    return float(h % 360)


def rssi_to_distance_m(rssi_dbm: float) -> float:
    return float(math.pow(10.0, (TX_POWER_DBM - rssi_dbm) / (10.0 * PATH_LOSS_N)))


def polar_to_meters(bearing_deg: float, distance_m: float) -> tuple[float, float]:
    rad = math.radians(bearing_deg)
    dist = max(0.05, distance_m)
    return dist * math.sin(rad), dist * math.cos(rad)


def meters_to_normalized(
    bearing_deg: float,
    distance_m: float,
    map_radius_m: float = DEFAULT_MAP_RADIUS_M,
) -> tuple[float, float]:
    east, north = polar_to_meters(bearing_deg, distance_m)
    span = max(5.0, map_radius_m) * 2.0
    x = max(0.0, min(1.0, 0.5 + east / span))
    y = max(0.0, min(1.0, 0.5 + north / span))
    return x, y


def build_router_positions(
    rssi_by_bssid: dict[str, float],
    bearings: dict[str, float],
    map_radius_m: float = DEFAULT_MAP_RADIUS_M,
) -> dict[str, tuple[float, float]]:
    """Map BSSID → normalized (x, y) using calibrated or estimated bearing + RSSI distance."""
    positions: dict[str, tuple[float, float]] = {}
    for bssid, rssi in rssi_by_bssid.items():
        bearing = bearings.get(bssid.upper()) or bearings.get(bssid)
        if bearing is None:
            bearing = stable_hash_bearing(bssid)
        dist = rssi_to_distance_m(float(rssi))
        positions[bssid] = meters_to_normalized(bearing, dist, map_radius_m)
    return positions


def positions_from_signals(
    signals: list[dict],
    bearings: dict[str, float],
    map_radius_m: float = DEFAULT_MAP_RADIUS_M,
) -> dict[str, tuple[float, float]]:
    rssi_map: dict[str, float] = {}
    for s in signals:
        bssid = s.get("bssid", "")
        if not bssid:
            continue
        rssi = s.get("raw_rssi", s.get("smoothed_rssi", -70))
        rssi_map[bssid] = float(rssi)
    return build_router_positions(rssi_map, bearings, map_radius_m)
