namespace StarSensing.Core.Models;

/// <summary>Batch-level environment snapshot persisted for historical replay and model training.</summary>
public sealed class EnvironmentBatchRecord
{
    public string BatchId { get; init; } = "";
    public long TimestampMs { get; init; }
    public double MotionConfidence { get; init; }
    public double OccupancyConfidence { get; init; }
    public int Classification { get; init; }
    public double StabilityIndex { get; init; }
    public double LstmMotionConfidence { get; init; }
    public double CnnActivityScore { get; init; }
    public int ActiveApCount { get; init; }
    public string Source { get; init; } = "Fallback";
}
