"""Per-router and batch-level Wi-Fi sensing features."""

import numpy as np


def shannon_entropy(values: np.ndarray, bins: int = 8) -> float:
    """Shannon entropy of RSSI samples (higher = less stable)."""
    if len(values) < 2:
        return 0.0
    lo, hi = float(np.min(values)), float(np.max(values))
    if hi - lo < 1e-6:
        return 0.0
    hist, _ = np.histogram(values, bins=bins, range=(lo, hi + 1e-6), density=True)
    hist = hist[hist > 0]
    if len(hist) == 0:
        return 0.0
    return float(-np.sum(hist * np.log2(hist + 1e-12)))


def compute_cross_correlations(processed_signals: list[dict]) -> tuple[dict[str, float], float]:
    """
    Pearson correlation between router RSSI windows.
    Returns per-BSSID mean correlation with others, and batch-level average.
    """
    if len(processed_signals) < 2:
        bssid = processed_signals[0]["bssid"] if processed_signals else ""
        return ({bssid: 0.0} if bssid else {}), 0.0

    bssids = [s["bssid"] for s in processed_signals]
    # Use recent RSSI history stored on each signal during processing.
    series = []
    valid_bssids = []
    for s in processed_signals:
        hist = s.get("_history")
        if hist is None or len(hist) < 3:
            continue
        valid_bssids.append(s["bssid"])
        series.append(np.asarray(hist, dtype=float))

    if len(series) < 2:
        return {b: 0.0 for b in bssids}, 0.0

    min_len = min(len(s) for s in series)
    matrix = np.vstack([s[-min_len:] for s in series])
    # Drop flat series — they cause divide-by-zero in np.corrcoef.
    active = [i for i, row in enumerate(matrix) if np.std(row) > 1e-6]
    if len(active) < 2:
        return {b: 0.0 for b in bssids}, 0.0
    matrix = matrix[active]
    active_bssids = [valid_bssids[i] for i in active]

    with np.errstate(invalid="ignore", divide="ignore"):
        corr = np.corrcoef(matrix)
    corr = np.nan_to_num(corr, nan=0.0)
    if corr.ndim < 2:
        return {b: 0.0 for b in bssids}, 0.0

    per_router: dict[str, float] = {}
    batch_vals = []
    for i, bssid in enumerate(active_bssids):
        others = [corr[i, j] for j in range(len(active_bssids)) if j != i]
        val = float(np.mean(others)) if others else 0.0
        if np.isnan(val):
            val = 0.0
        per_router[bssid] = val
        batch_vals.append(val)

    for b in bssids:
        per_router.setdefault(b, 0.0)

    batch_avg = float(np.mean(batch_vals)) if batch_vals else 0.0
    return per_router, batch_avg


def enrich_signals(processed_signals: list[dict]) -> tuple[list[dict], float]:
    """Add entropy and cross_correlation to each processed signal dict."""
    for s in processed_signals:
        hist = s.get("_history")
        if hist is not None and len(hist) >= 2:
            s["entropy"] = shannon_entropy(np.asarray(hist, dtype=float))
        else:
            s["entropy"] = 0.0

    per_router, batch_corr = compute_cross_correlations(processed_signals)
    for s in processed_signals:
        s["cross_correlation"] = per_router.get(s["bssid"], 0.0)
        s.pop("_history", None)

    return processed_signals, batch_corr


def stability_index(processed_signals: list[dict]) -> float:
    """0 = chaotic, 1 = perfectly stable."""
    if not processed_signals:
        return 1.0
    entropies = [s.get("entropy", 0.0) for s in processed_signals]
    max_ent = max(entropies) if entropies else 0.0
    # Typical entropy range for 8 bins ~ 0..3
    return float(max(0.0, min(1.0, 1.0 - max_ent / 3.0)))
