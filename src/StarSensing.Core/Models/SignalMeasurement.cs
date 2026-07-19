namespace StarSensing.Core.Models;

/// <summary>
/// A single RSSI measurement for a specific BSSID at a point in time.
/// Used for time-series analysis and signal processing.
/// </summary>
public sealed class SignalMeasurement
{
    /// <summary>BSSID of the access point being measured.</summary>
    public string Bssid { get; set; } = string.Empty;

    /// <summary>SSID of the network.</summary>
    public string Ssid { get; set; } = string.Empty;

    /// <summary>Raw RSSI value in dBm.</summary>
    public int RssiDbm { get; set; }

    /// <summary>Signal quality percentage (0-100).</summary>
    public int SignalQuality { get; set; }

    /// <summary>Channel number.</summary>
    public int Channel { get; set; }

    /// <summary>Frequency in kHz.</summary>
    public int FrequencyKHz { get; set; }

    /// <summary>UTC timestamp of measurement.</summary>
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Unix epoch milliseconds — useful for fast time-series indexing.</summary>
    public long TimestampMs => Timestamp.ToUnixTimeMilliseconds();

    /// <summary>Smoothed RSSI after moving average (set by signal processor).</summary>
    public double? SmoothedRssi { get; set; }

    /// <summary>Variance over recent window (set by signal processor).</summary>
    public double? Variance { get; set; }
}

/// <summary>
/// A batch of measurements from a single scan cycle.
/// </summary>
public sealed class ScanBatch
{
    /// <summary>Unique batch identifier.</summary>
    public Guid BatchId { get; set; } = Guid.NewGuid();

    /// <summary>When this scan was performed.</summary>
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>All networks detected in this scan.</summary>
    public List<SignalMeasurement> Measurements { get; set; } = new();

    /// <summary>Scan duration in milliseconds.</summary>
    public int ScanDurationMs { get; set; }

    /// <summary>Number of networks detected.</summary>
    public int NetworkCount => Measurements.Count;
}
