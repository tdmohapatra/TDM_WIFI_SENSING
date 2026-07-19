/*
  StarSensing — purge non-training data to reclaim disk space.

  KEEPS (ML training + calibration):
    - WiFi_Features   (LSTM, CNN, zone inputs)
    - Zone_State      (zone model labels)
    - RouterBearings  (map calibration — never time-purged)
    - MapSettings     (map north offset — never time-purged)
    - AccessPoints    (AP metadata — small; optional stale prune)

  DELETES aggressively (not used for training):
    - Measurements         (raw RSSI — largest table)
    - Motion_Events        (dashboard replay only)
    - Environment_Batches  (dashboard replay only)

  Usage:
    EXEC dbo.PurgeNonTrainingData @DryRun = 1;                    -- preview counts
    EXEC dbo.PurgeNonTrainingData;                                -- defaults
    EXEC dbo.PurgeNonTrainingData @RawRetentionHours = 12;        -- tighter raw window
    EXEC dbo.PurgeNonTrainingData @TrainingRetentionHours = 336;  -- keep 14 days training
*/

SET NOCOUNT ON;
GO

CREATE OR ALTER PROCEDURE dbo.GetDataStorageStats
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @stats TABLE (TableName NVARCHAR(64), Cnt BIGINT, EstimatedMB DECIMAL(18,2));

    IF OBJECT_ID(N'dbo.Measurements', N'U') IS NOT NULL
        INSERT @stats SELECT N'Measurements', COUNT_BIG(*),
            CAST(SUM(DATALENGTH(COALESCE(Ssid, N'')) + 8) / 1048576.0 AS DECIMAL(18,2))
        FROM dbo.Measurements WITH (NOLOCK);

    IF OBJECT_ID(N'dbo.WiFi_Features', N'U') IS NOT NULL
        INSERT @stats SELECT N'WiFi_Features', COUNT_BIG(*),
            CAST(COUNT_BIG(*) * 200 / 1048576.0 AS DECIMAL(18,2))
        FROM dbo.WiFi_Features WITH (NOLOCK);

    IF OBJECT_ID(N'dbo.Zone_State', N'U') IS NOT NULL
        INSERT @stats SELECT N'Zone_State', COUNT_BIG(*),
            CAST(COUNT_BIG(*) * 120 / 1048576.0 AS DECIMAL(18,2))
        FROM dbo.Zone_State WITH (NOLOCK);

    IF OBJECT_ID(N'dbo.Motion_Events', N'U') IS NOT NULL
        INSERT @stats SELECT N'Motion_Events', COUNT_BIG(*),
            CAST(COUNT_BIG(*) * 300 / 1048576.0 AS DECIMAL(18,2))
        FROM dbo.Motion_Events WITH (NOLOCK);

    IF OBJECT_ID(N'dbo.Environment_Batches', N'U') IS NOT NULL
        INSERT @stats SELECT N'Environment_Batches', COUNT_BIG(*),
            CAST(COUNT_BIG(*) * 80 / 1048576.0 AS DECIMAL(18,2))
        FROM dbo.Environment_Batches WITH (NOLOCK);

    IF OBJECT_ID(N'dbo.RouterBearings', N'U') IS NOT NULL
        INSERT @stats SELECT N'RouterBearings', COUNT_BIG(*),
            CAST(COUNT_BIG(*) * 64 / 1048576.0 AS DECIMAL(18,2))
        FROM dbo.RouterBearings WITH (NOLOCK);

    IF OBJECT_ID(N'dbo.AccessPoints', N'U') IS NOT NULL
        INSERT @stats SELECT N'AccessPoints', COUNT_BIG(*),
            CAST(COUNT_BIG(*) * 128 / 1048576.0 AS DECIMAL(18,2))
        FROM dbo.AccessPoints WITH (NOLOCK);

    SELECT TableName, Cnt AS [RowCount], EstimatedMB FROM @stats ORDER BY Cnt DESC;
END
GO

CREATE OR ALTER PROCEDURE dbo.PurgeNonTrainingData
    @TrainingRetentionHours INT = 168,   -- WiFi_Features + Zone_State (7 days)
    @RawRetentionHours      INT = 24,    -- Measurements (raw RSSI)
    @ReplayRetentionHours    INT = 48,    -- Motion_Events + Environment_Batches
    @AccessPointMaxAgeDays   INT = 30,   -- 0 = skip AccessPoints prune
    @BatchSize               INT = 50000,
    @DryRun                  BIT = 0
AS
BEGIN
    SET NOCOUNT ON;

    IF @TrainingRetentionHours < 1 OR @RawRetentionHours < 1 OR @ReplayRetentionHours < 1
    BEGIN
        RAISERROR(N'Retention hours must be >= 1.', 16, 1);
        RETURN;
    END

    IF @BatchSize < 1000 SET @BatchSize = 1000;

    DECLARE @nowMs BIGINT = DATEDIFF_BIG(millisecond, '19700101', SYSUTCDATETIME());
    DECLARE @trainingCutoff BIGINT = @nowMs - CAST(@TrainingRetentionHours AS BIGINT) * 3600000;
    DECLARE @rawCutoff BIGINT      = @nowMs - CAST(@RawRetentionHours AS BIGINT) * 3600000;
    DECLARE @replayCutoff BIGINT    = @nowMs - CAST(@ReplayRetentionHours AS BIGINT) * 3600000;
    DECLARE @apCutoffMs BIGINT      = @nowMs - CAST(@AccessPointMaxAgeDays AS BIGINT) * 86400000;

    DECLARE @delMeasurements BIGINT = 0;
    DECLARE @delMotionEvents BIGINT = 0;
    DECLARE @delEnvBatches BIGINT = 0;
    DECLARE @delFeatures BIGINT = 0;
    DECLARE @delZones BIGINT = 0;
    DECLARE @delAccessPoints BIGINT = 0;
    DECLARE @batch INT;

    IF @DryRun = 1
    BEGIN
        SELECT @delMeasurements = COUNT_BIG(*) FROM dbo.Measurements WHERE TimestampMs < @rawCutoff;
        SELECT @delMotionEvents = COUNT_BIG(*) FROM dbo.Motion_Events WHERE TimestampMs < @replayCutoff;
        SELECT @delEnvBatches = COUNT_BIG(*) FROM dbo.Environment_Batches WHERE TimestampMs < @replayCutoff;
        SELECT @delFeatures = COUNT_BIG(*) FROM dbo.WiFi_Features WHERE TimestampMs < @trainingCutoff;
        SELECT @delZones = COUNT_BIG(*) FROM dbo.Zone_State WHERE TimestampMs < @trainingCutoff;
        IF @AccessPointMaxAgeDays > 0
            SELECT @delAccessPoints = COUNT_BIG(*) FROM dbo.AccessPoints WHERE LastSeenMs < @apCutoffMs;

        SELECT
            @delMeasurements AS MeasurementsToDelete,
            @delMotionEvents AS MotionEventsToDelete,
            @delEnvBatches AS EnvironmentBatchesToDelete,
            @delFeatures AS WiFiFeaturesToDelete,
            @delZones AS ZoneStateToDelete,
            @delAccessPoints AS AccessPointsToDelete,
            @rawCutoff AS RawCutoffMs,
            @replayCutoff AS ReplayCutoffMs,
            @trainingCutoff AS TrainingCutoffMs,
            CAST(1 AS BIT) AS DryRun;
        RETURN;
    END

    /* Non-training: raw scans (largest) */
    SET @batch = @BatchSize;
    WHILE @batch = @BatchSize
    BEGIN
        DELETE TOP (@BatchSize) FROM dbo.Measurements WHERE TimestampMs < @rawCutoff;
        SET @batch = @@ROWCOUNT;
        SET @delMeasurements += @batch;
    END

    /* Non-training: dashboard replay */
    SET @batch = @BatchSize;
    WHILE @batch = @BatchSize
    BEGIN
        DELETE TOP (@BatchSize) FROM dbo.Motion_Events WHERE TimestampMs < @replayCutoff;
        SET @batch = @@ROWCOUNT;
        SET @delMotionEvents += @batch;
    END

    SET @batch = @BatchSize;
    WHILE @batch = @BatchSize
    BEGIN
        DELETE TOP (@BatchSize) FROM dbo.Environment_Batches WHERE TimestampMs < @replayCutoff;
        SET @batch = @@ROWCOUNT;
        SET @delEnvBatches += @batch;
    END

    /* Training tables — longer retention */
    SET @batch = @BatchSize;
    WHILE @batch = @BatchSize
    BEGIN
        DELETE TOP (@BatchSize) FROM dbo.Zone_State WHERE TimestampMs < @trainingCutoff;
        SET @batch = @@ROWCOUNT;
        SET @delZones += @batch;
    END

    SET @batch = @BatchSize;
    WHILE @batch = @BatchSize
    BEGIN
        DELETE TOP (@BatchSize) FROM dbo.WiFi_Features WHERE TimestampMs < @trainingCutoff;
        SET @batch = @@ROWCOUNT;
        SET @delFeatures += @batch;
    END

    /* Optional: stale AP metadata (not used for ML) */
    IF @AccessPointMaxAgeDays > 0
    BEGIN
        DELETE FROM dbo.AccessPoints WHERE LastSeenMs < @apCutoffMs;
        SET @delAccessPoints = @@ROWCOUNT;
    END

    SELECT
        @delMeasurements AS MeasurementsDeleted,
        @delMotionEvents AS MotionEventsDeleted,
        @delEnvBatches AS EnvironmentBatchesDeleted,
        @delFeatures AS WiFiFeaturesDeleted,
        @delZones AS ZoneStateDeleted,
        @delAccessPoints AS AccessPointsDeleted,
        @rawCutoff AS RawCutoffMs,
        @replayCutoff AS ReplayCutoffMs,
        @trainingCutoff AS TrainingCutoffMs,
        CAST(0 AS BIT) AS DryRun;
END
GO
