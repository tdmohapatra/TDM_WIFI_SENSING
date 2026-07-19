namespace StarSensing.Core.Models;

/// <summary>Row counts removed by dbo.PurgeNonTrainingData.</summary>
public sealed record DataPurgeResult
{
    public long MeasurementsDeleted { get; init; }
    public long MotionEventsDeleted { get; init; }
    public long EnvironmentBatchesDeleted { get; init; }
    public long WiFiFeaturesDeleted { get; init; }
    public long ZoneStateDeleted { get; init; }
    public long AccessPointsDeleted { get; init; }
    public bool DryRun { get; init; }

    public long TotalDeleted =>
        MeasurementsDeleted + MotionEventsDeleted + EnvironmentBatchesDeleted
        + WiFiFeaturesDeleted + ZoneStateDeleted + AccessPointsDeleted;
}
