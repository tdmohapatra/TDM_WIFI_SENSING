namespace StarSensing.Dashboard.Models;

/// <summary>One AP row for the spatial heatmap side list.</summary>
public sealed class SpatialApRow
{
    public string Ssid { get; init; } = "";
    public string Bssid { get; init; } = "";
    public int RssiDbm { get; init; }
    public double DistanceMeters { get; init; }
    public double BearingDeg { get; init; }
    public double Activity { get; init; }
    public double Variance { get; init; }
    public double NormalizedX { get; init; }
    public double NormalizedY { get; init; }
    public string BearingSource { get; init; } = "";

    public string Summary =>
        $"{RssiDbm} dBm · {DistanceMeters:F1} m · {BearingDeg:F0}° · σ² {Variance:F2}";
}
