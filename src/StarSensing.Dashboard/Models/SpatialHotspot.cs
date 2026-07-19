namespace StarSensing.Dashboard.Models;

public enum SpatialHotspotKind { Motion, Obstacle }

/// <summary>Persistent accumulated motion or obstacle location from spatial memory.</summary>
public sealed class SpatialHotspot
{
    public SpatialHotspotKind Kind { get; init; }
    public string Label { get; init; } = "";
    public double NormalizedX { get; init; }
    public double NormalizedY { get; init; }
    public double Score { get; init; }
    public int HitCount { get; init; }
    public double DistanceMeters { get; init; }
    public double BearingDeg { get; init; }

    public string Summary => DistanceMeters < 1.0
        ? $"{HitCount} hits · {DistanceMeters * 100:F0} cm · {BearingDeg:F0}° · score {Score:F2}"
        : $"{HitCount} hits · {DistanceMeters:F2} m · {BearingDeg:F0}° · score {Score:F2}";

    public string DistanceLabel => DistanceMeters < 1.0
        ? $"{DistanceMeters * 100:F0} cm"
        : $"{DistanceMeters:F2} m";
}
