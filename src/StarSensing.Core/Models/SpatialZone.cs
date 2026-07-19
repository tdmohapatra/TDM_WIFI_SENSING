namespace StarSensing.Core.Models;

/// <summary>
/// Represents a spatial zone for coarse environmental modeling.
/// Zones are defined by signal fingerprints from multiple APs.
/// </summary>
public sealed class SpatialZone
{
    /// <summary>Unique zone identifier.</summary>
    public string ZoneId { get; set; } = string.Empty;

    /// <summary>User-friendly zone name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Estimated X position in the 2D model (normalized 0-1).</summary>
    public double X { get; set; }

    /// <summary>Estimated Y position in the 2D model (normalized 0-1).</summary>
    public double Y { get; set; }

    /// <summary>Estimated radius of the zone (normalized 0-1).</summary>
    public double Radius { get; set; } = 0.1;

    /// <summary>Occupancy confidence (0.0 to 1.0).</summary>
    public double OccupancyConfidence { get; set; }

    /// <summary>Motion confidence (0.0 to 1.0).</summary>
    public double MotionConfidence { get; set; }

    /// <summary>Signal fingerprint: BSSID → expected RSSI range.</summary>
    public Dictionary<string, SignalFingerprint> Fingerprints { get; set; } = new();

    /// <summary>Last time this zone had activity.</summary>
    public DateTimeOffset LastActivity { get; set; }

    /// <summary>Color for visualization (hex string).</summary>
    public string Color { get; set; } = "#00FF88";
}

/// <summary>
/// A signal fingerprint for a specific AP within a zone.
/// </summary>
public sealed class SignalFingerprint
{
    /// <summary>BSSID of the access point.</summary>
    public string Bssid { get; set; } = string.Empty;

    /// <summary>Average expected RSSI in this zone.</summary>
    public double MeanRssi { get; set; }

    /// <summary>Standard deviation of RSSI in this zone.</summary>
    public double StdDevRssi { get; set; }

    /// <summary>Number of samples used to build this fingerprint.</summary>
    public int SampleCount { get; set; }
}
