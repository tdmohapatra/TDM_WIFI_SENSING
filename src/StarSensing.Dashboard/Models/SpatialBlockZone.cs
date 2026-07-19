namespace StarSensing.Dashboard.Models;

/// <summary>Real-time detection block for the spatial heatmap surround view.</summary>
public sealed class SpatialBlockZone
{
    public string ZoneId { get; init; } = "";
    public string Name { get; init; } = "";
    public double NormalizedX { get; init; }
    public double NormalizedY { get; init; }
    public double RadiusNorm { get; init; }
    public double MotionPct { get; init; }
    public double OccupancyPct { get; init; }
    public double DistanceMeters { get; init; }
    public double BearingDeg { get; init; }

    public string Summary =>
        $"{DistanceMeters:F2} m · {BearingDeg:F0}° · Motion {MotionPct:F0}% · Occ {OccupancyPct:F0}%";

    public string DistanceLabel => DistanceMeters < 1.0
        ? $"{DistanceMeters * 100:F0} cm"
        : $"{DistanceMeters:F2} m";
}
