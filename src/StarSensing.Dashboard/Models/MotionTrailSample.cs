namespace StarSensing.Dashboard.Models;

public sealed class MotionTrailSample
{
    public string ZoneId { get; init; } = "";
    public double NormalizedX { get; init; }
    public double NormalizedY { get; init; }
    public float Strength { get; init; }
    public float Age { get; set; }
    public long Sequence { get; init; }
    public bool IsObstacle { get; init; }
}
