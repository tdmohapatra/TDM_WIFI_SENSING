namespace StarSensing.Dashboard.Models;

public sealed class ZoneDisplayItem
{
    public string ZoneId { get; init; } = "";
    public string Name { get; init; } = "";
    public double X { get; init; }
    public double Y { get; init; }
    public double Radius { get; init; }
    public double OccupancyPct { get; init; }
    public double MotionPct { get; init; }
    public string ColorHex { get; init; } = "#4361ee";
    public string Summary { get; init; } = "";
}