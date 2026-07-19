using StarSensing.Core.Models;

namespace StarSensing.Core.Interfaces;

/// <summary>
/// Interface for signal measurement persistence.
/// </summary>
public interface ISignalStore
{
    /// <summary>Stores a batch of measurements.</summary>
    Task StoreBatchAsync(ScanBatch batch, CancellationToken ct = default);

    /// <summary>Gets measurements for a specific BSSID within a time range.</summary>
    Task<IReadOnlyList<SignalMeasurement>> GetMeasurementsAsync(
        string bssid,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken ct = default);

    /// <summary>Gets the most recent measurements for all networks.</summary>
    Task<IReadOnlyList<SignalMeasurement>> GetLatestMeasurementsAsync(
        int maxAge_seconds = 60,
        CancellationToken ct = default);

    /// <summary>Gets saved access point metadata and aggregate statistics.</summary>
    Task<IReadOnlyList<AccessPointDetails>> GetSavedAccessPointsAsync(
        int maxAge_seconds = 0,
        CancellationToken ct = default);

    /// <summary>Gets all unique BSSIDs seen in the given time window.</summary>
    Task<IReadOnlyList<string>> GetActiveBssidsAsync(
        int maxAge_seconds = 300,
        CancellationToken ct = default);

    /// <summary>Purges data older than the specified retention period.</summary>
    Task PurgeOldDataAsync(TimeSpan retention, CancellationToken ct = default);

    /// <summary>
    /// Purges non-training data aggressively while keeping ML tables longer.
    /// Deletes Measurements / Motion_Events / Environment_Batches sooner than WiFi_Features / Zone_State.
    /// </summary>
    Task<DataPurgeResult> PurgeNonTrainingDataAsync(
        int trainingRetentionHours = 168,
        int rawRetentionHours = 24,
        int replayRetentionHours = 48,
        int accessPointMaxAgeDays = 30,
        bool dryRun = false,
        CancellationToken ct = default);

    /// <summary>Initializes the data store (creates tables, etc).</summary>
    Task InitializeAsync(CancellationToken ct = default);

    /// <summary>Persists computed feature vectors for a batch.</summary>
    Task StoreFeaturesAsync(IReadOnlyList<WiFiFeatureRecord> features, CancellationToken ct = default);

    /// <summary>Persists motion detection events.</summary>
    Task StoreMotionEventsAsync(string batchId, long timestampMs, IReadOnlyList<MotionEvent> events, CancellationToken ct = default);

    /// <summary>Persists spatial zone snapshots.</summary>
    Task StoreZoneStatesAsync(string batchId, long timestampMs, IReadOnlyList<SpatialZone> zones, CancellationToken ct = default);

    /// <summary>Persists batch-level environment metrics (motion, LSTM, CNN, classification).</summary>
    Task StoreEnvironmentBatchAsync(EnvironmentBatchRecord batch, CancellationToken ct = default);
}
