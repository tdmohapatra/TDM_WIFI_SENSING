"""Load calibrated router bearings from SQL (RouterBearings + MapSettings)."""

from __future__ import annotations

import logging
import os
import time

from map_geometry import DEFAULT_MAP_RADIUS_M

logger = logging.getLogger(__name__)

DEFAULT_CS = (
    "Server=localhost;Database=StarSensing;Trusted_Connection=yes;"
    "TrustServerCertificate=yes;Encrypt=no;"
)


class BearingStore:
    def __init__(
        self,
        connection_string: str | None = None,
        refresh_interval_sec: float = 60.0,
        map_radius_m: float = DEFAULT_MAP_RADIUS_M,
    ):
        self._cs = connection_string or os.environ.get("STAR_SENSING_CS", DEFAULT_CS)
        self._refresh_interval = refresh_interval_sec
        self._map_radius_m = map_radius_m
        self._bearings: dict[str, float] = {}
        self._last_refresh = 0.0
        self.refresh(force=True)

    @property
    def map_radius_m(self) -> float:
        return self._map_radius_m

    @property
    def bearings(self) -> dict[str, float]:
        self.refresh()
        return self._bearings

    def refresh(self, force: bool = False) -> None:
        if not force and (time.monotonic() - self._last_refresh) < self._refresh_interval:
            return
        try:
            import pyodbc  # type: ignore
        except ImportError:
            return

        cs = self._cs.strip()
        if "DRIVER=" not in cs.upper():
            cs = f"DRIVER={{ODBC Driver 17 for SQL Server}};{cs}"

        try:
            conn = pyodbc.connect(cs)
            cur = conn.cursor()
            bearings: dict[str, float] = {}
            try:
                cur.execute("SELECT Bssid, BearingDeg FROM dbo.RouterBearings;")
                for row in cur.fetchall():
                    bearings[str(row[0]).upper()] = float(row[1])
            except Exception:
                pass

            try:
                cur.execute(
                    "SELECT SettingValue FROM dbo.MapSettings WHERE SettingKey = N'NorthOffsetDeg';"
                )
                row = cur.fetchone()
                if row and row[0] is not None:
                    # North offset is applied at capture time in dashboard; bearings are stored map-ready.
                    _ = float(row[0])
            except Exception:
                pass

            conn.close()
            self._bearings = bearings
            self._last_refresh = time.monotonic()
            if bearings:
                logger.debug("Loaded %d calibrated bearings from SQL.", len(bearings))
        except Exception as ex:
            logger.debug("Bearing refresh failed: %s", ex)


def load_bearings_from_sql(connection_string: str | None = None) -> dict[str, float]:
    store = BearingStore(connection_string=connection_string, refresh_interval_sec=1e9)
    return dict(store.bearings)
