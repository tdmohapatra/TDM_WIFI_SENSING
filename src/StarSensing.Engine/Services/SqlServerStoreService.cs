using Microsoft.Data.SqlClient;
using StarSensing.Core.Interfaces;
using StarSensing.Core.Models;

namespace StarSensing.Engine.Services;

/// <summary>
/// SQL Server (localhost) backed signal store. Auto-creates the database and tables
/// on startup. Connection is configured via ConnectionStrings:StarSensing.
/// </summary>
public class SqlServerStoreService : ISignalStore, IDisposable
{
    private readonly string _connectionString;
    private readonly string _masterConnectionString;
    private readonly string _databaseName;
    private readonly ILogger<SqlServerStoreService> _logger;
    private SqlConnection? _connection;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public SqlServerStoreService(IConfiguration config, ILogger<SqlServerStoreService> logger)
    {
        _logger = logger;

        var configured = config.GetConnectionString("StarSensing")
            ?? config.GetValue<string>("SensingConfig:SqlConnectionString")
            ?? "Server=localhost;Database=StarSensing;Trusted_Connection=True;TrustServerCertificate=True;Encrypt=False;";

        var builder = new SqlConnectionStringBuilder(configured);
        _databaseName = string.IsNullOrWhiteSpace(builder.InitialCatalog) ? "StarSensing" : builder.InitialCatalog;
        builder.InitialCatalog = _databaseName;
        _connectionString = builder.ConnectionString;

        var master = new SqlConnectionStringBuilder(configured) { InitialCatalog = "master" };
        _masterConnectionString = master.ConnectionString;
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            // 1) Ensure the database exists (connect to master).
            await using (var master = new SqlConnection(_masterConnectionString))
            {
                await master.OpenAsync(ct);
                await using var createDb = master.CreateCommand();
                createDb.CommandText = $@"
                    IF DB_ID(N'{_databaseName}') IS NULL
                        CREATE DATABASE [{_databaseName}];";
                await createDb.ExecuteNonQueryAsync(ct);
            }

            // 2) Open the working connection and create tables.
            _connection = new SqlConnection(_connectionString);
            await _connection.OpenAsync(ct);

            await using var cmd = _connection.CreateCommand();
            cmd.CommandText = @"
                IF OBJECT_ID(N'dbo.Measurements', N'U') IS NULL
                CREATE TABLE dbo.Measurements (
                    Id            BIGINT IDENTITY(1,1) PRIMARY KEY,
                    Bssid         NVARCHAR(64)  NOT NULL,
                    Ssid          NVARCHAR(256) NOT NULL,
                    RssiDbm       INT           NOT NULL,
                    SignalQuality INT           NOT NULL DEFAULT 0,
                    Channel       INT           NOT NULL,
                    FrequencyKHz  INT           NOT NULL,
                    TimestampMs   BIGINT        NOT NULL
                );

                IF OBJECT_ID(N'dbo.AccessPoints', N'U') IS NULL
                CREATE TABLE dbo.AccessPoints (
                    Bssid         NVARCHAR(64)  NOT NULL PRIMARY KEY,
                    Ssid          NVARCHAR(256) NOT NULL,
                    LatestRssiDbm INT           NOT NULL,
                    SignalQuality INT           NOT NULL,
                    Channel       INT           NOT NULL,
                    FrequencyKHz  INT           NOT NULL,
                    Band          NVARCHAR(16)  NOT NULL,
                    FirstSeenMs   BIGINT        NOT NULL,
                    LastSeenMs    BIGINT        NOT NULL,
                    MinRssiDbm    INT           NOT NULL,
                    MaxRssiDbm    INT           NOT NULL,
                    SampleCount   BIGINT        NOT NULL
                );

                IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IDX_Measurements_Bssid_Timestamp')
                    CREATE INDEX IDX_Measurements_Bssid_Timestamp ON dbo.Measurements(Bssid, TimestampMs);
                IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IDX_Measurements_Timestamp')
                    CREATE INDEX IDX_Measurements_Timestamp ON dbo.Measurements(TimestampMs);
                IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IDX_AccessPoints_LastSeen')
                    CREATE INDEX IDX_AccessPoints_LastSeen ON dbo.AccessPoints(LastSeenMs);

                IF OBJECT_ID(N'dbo.WiFi_Features', N'U') IS NULL
                CREATE TABLE dbo.WiFi_Features (
                    Id               BIGINT IDENTITY(1,1) PRIMARY KEY,
                    BatchId          NVARCHAR(64)  NOT NULL,
                    Bssid            NVARCHAR(64)  NOT NULL,
                    Ssid             NVARCHAR(256) NOT NULL,
                    TimestampMs      BIGINT        NOT NULL,
                    RawRssi          INT           NOT NULL,
                    SmoothedRssi     FLOAT         NOT NULL,
                    Variance         FLOAT         NOT NULL,
                    StdDev           FLOAT         NOT NULL,
                    ChangeRate       FLOAT         NOT NULL,
                    Entropy          FLOAT         NOT NULL DEFAULT 0,
                    CrossCorrelation FLOAT         NOT NULL DEFAULT 0,
                    DominantFrequency FLOAT        NOT NULL DEFAULT 0,
                    SpectralEnergy   FLOAT         NOT NULL DEFAULT 0,
                    ZScore           FLOAT         NOT NULL DEFAULT 0,
                    IsAnomaly        BIT           NOT NULL DEFAULT 0,
                    MotionConfidence FLOAT         NOT NULL DEFAULT 0
                );

                IF OBJECT_ID(N'dbo.Motion_Events', N'U') IS NULL
                CREATE TABLE dbo.Motion_Events (
                    Id               BIGINT IDENTITY(1,1) PRIMARY KEY,
                    EventId          UNIQUEIDENTIFIER NOT NULL,
                    BatchId          NVARCHAR(64)  NOT NULL,
                    TimestampMs      BIGINT        NOT NULL,
                    EventType        INT           NOT NULL,
                    Confidence       FLOAT         NOT NULL,
                    Description      NVARCHAR(512) NOT NULL,
                    AffectedApCount  INT           NOT NULL,
                    AffectedBssids   NVARCHAR(MAX) NULL,
                    AverageVariance  FLOAT         NOT NULL DEFAULT 0,
                    PeakRssiChange   FLOAT         NOT NULL DEFAULT 0,
                    DurationMs       INT           NOT NULL DEFAULT 0
                );

                IF OBJECT_ID(N'dbo.Zone_State', N'U') IS NULL
                CREATE TABLE dbo.Zone_State (
                    Id                   BIGINT IDENTITY(1,1) PRIMARY KEY,
                    TimestampMs          BIGINT        NOT NULL,
                    ZoneId               NVARCHAR(64)  NOT NULL,
                    Name                 NVARCHAR(128) NOT NULL,
                    X                    FLOAT         NOT NULL,
                    Y                    FLOAT         NOT NULL,
                    Radius               FLOAT         NOT NULL,
                    OccupancyConfidence  FLOAT         NOT NULL,
                    MotionConfidence     FLOAT         NOT NULL,
                    Color                NVARCHAR(16)  NOT NULL
                );

                IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IDX_WiFi_Features_Timestamp')
                    CREATE INDEX IDX_WiFi_Features_Timestamp ON dbo.WiFi_Features(TimestampMs);
                IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IDX_WiFi_Features_Bssid')
                    CREATE INDEX IDX_WiFi_Features_Bssid ON dbo.WiFi_Features(Bssid, TimestampMs);
                IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IDX_Motion_Events_Timestamp')
                    CREATE INDEX IDX_Motion_Events_Timestamp ON dbo.Motion_Events(TimestampMs);
                IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IDX_Zone_State_Timestamp')
                    CREATE INDEX IDX_Zone_State_Timestamp ON dbo.Zone_State(TimestampMs);

                IF OBJECT_ID(N'dbo.Environment_Batches', N'U') IS NULL
                CREATE TABLE dbo.Environment_Batches (
                    Id                   BIGINT IDENTITY(1,1) PRIMARY KEY,
                    BatchId              NVARCHAR(64)  NOT NULL,
                    TimestampMs          BIGINT        NOT NULL,
                    MotionConfidence     FLOAT         NOT NULL,
                    OccupancyConfidence  FLOAT         NOT NULL,
                    Classification       INT           NOT NULL,
                    StabilityIndex       FLOAT         NOT NULL,
                    LstmMotionConfidence FLOAT         NOT NULL DEFAULT 0,
                    CnnActivityScore     FLOAT         NOT NULL DEFAULT 0,
                    ActiveApCount        INT           NOT NULL,
                    Source               NVARCHAR(16)  NOT NULL DEFAULT 'Fallback'
                );

                IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IDX_Environment_Batches_Timestamp')
                    CREATE INDEX IDX_Environment_Batches_Timestamp ON dbo.Environment_Batches(TimestampMs);
                IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IDX_Environment_Batches_BatchId')
                    CREATE INDEX IDX_Environment_Batches_BatchId ON dbo.Environment_Batches(BatchId);

                IF COL_LENGTH(N'dbo.Zone_State', N'BatchId') IS NULL
                    ALTER TABLE dbo.Zone_State ADD BatchId NVARCHAR(64) NULL;

                IF OBJECT_ID(N'dbo.WiFi_Scan_Raw', N'V') IS NULL
                    EXEC(N'CREATE VIEW dbo.WiFi_Scan_Raw AS
                          SELECT Id, Bssid, Ssid, RssiDbm, SignalQuality, Channel, FrequencyKHz, TimestampMs
                          FROM dbo.Measurements');";
            await cmd.ExecuteNonQueryAsync(ct);

            await DeployPurgeProceduresAsync(ct);

            _logger.LogInformation("SQL Server database '{Database}' initialized.", _databaseName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize SQL Server database. ConnectionString hint: {Hint}",
                _masterConnectionString);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task StoreBatchAsync(ScanBatch batch, CancellationToken ct = default)
    {
        if (_connection == null || batch.Measurements.Count == 0) return;

        await _lock.WaitAsync(ct);
        try
        {
            await using var transaction = (SqlTransaction)await _connection.BeginTransactionAsync(ct);

            await using var measurementCmd = _connection.CreateCommand();
            measurementCmd.Transaction = transaction;
            measurementCmd.CommandText = @"
                INSERT INTO dbo.Measurements (Bssid, Ssid, RssiDbm, SignalQuality, Channel, FrequencyKHz, TimestampMs)
                VALUES (@bssid, @ssid, @rssi, @quality, @channel, @freq, @ts);";

            var pBssid = measurementCmd.Parameters.Add("@bssid", System.Data.SqlDbType.NVarChar, 64);
            var pSsid = measurementCmd.Parameters.Add("@ssid", System.Data.SqlDbType.NVarChar, 256);
            var pRssi = measurementCmd.Parameters.Add("@rssi", System.Data.SqlDbType.Int);
            var pQuality = measurementCmd.Parameters.Add("@quality", System.Data.SqlDbType.Int);
            var pChannel = measurementCmd.Parameters.Add("@channel", System.Data.SqlDbType.Int);
            var pFreq = measurementCmd.Parameters.Add("@freq", System.Data.SqlDbType.Int);
            var pTs = measurementCmd.Parameters.Add("@ts", System.Data.SqlDbType.BigInt);

            await using var apCmd = _connection.CreateCommand();
            apCmd.Transaction = transaction;
            apCmd.CommandText = @"
                MERGE dbo.AccessPoints AS target
                USING (SELECT @ap_bssid AS Bssid) AS src
                ON target.Bssid = src.Bssid
                WHEN MATCHED THEN UPDATE SET
                    Ssid          = CASE WHEN @ap_ssid = N'' THEN target.Ssid ELSE @ap_ssid END,
                    LatestRssiDbm = @ap_rssi,
                    SignalQuality = @ap_quality,
                    Channel       = @ap_channel,
                    FrequencyKHz  = @ap_freq,
                    Band          = @ap_band,
                    LastSeenMs    = @ap_ts,
                    MinRssiDbm    = CASE WHEN @ap_rssi < target.MinRssiDbm THEN @ap_rssi ELSE target.MinRssiDbm END,
                    MaxRssiDbm    = CASE WHEN @ap_rssi > target.MaxRssiDbm THEN @ap_rssi ELSE target.MaxRssiDbm END,
                    SampleCount   = target.SampleCount + 1
                WHEN NOT MATCHED THEN
                    INSERT (Bssid, Ssid, LatestRssiDbm, SignalQuality, Channel, FrequencyKHz, Band,
                            FirstSeenMs, LastSeenMs, MinRssiDbm, MaxRssiDbm, SampleCount)
                    VALUES (@ap_bssid, @ap_ssid, @ap_rssi, @ap_quality, @ap_channel, @ap_freq, @ap_band,
                            @ap_ts, @ap_ts, @ap_rssi, @ap_rssi, 1);";

            var apBssid = apCmd.Parameters.Add("@ap_bssid", System.Data.SqlDbType.NVarChar, 64);
            var apSsid = apCmd.Parameters.Add("@ap_ssid", System.Data.SqlDbType.NVarChar, 256);
            var apRssi = apCmd.Parameters.Add("@ap_rssi", System.Data.SqlDbType.Int);
            var apQuality = apCmd.Parameters.Add("@ap_quality", System.Data.SqlDbType.Int);
            var apChannel = apCmd.Parameters.Add("@ap_channel", System.Data.SqlDbType.Int);
            var apFreq = apCmd.Parameters.Add("@ap_freq", System.Data.SqlDbType.Int);
            var apBand = apCmd.Parameters.Add("@ap_band", System.Data.SqlDbType.NVarChar, 16);
            var apTs = apCmd.Parameters.Add("@ap_ts", System.Data.SqlDbType.BigInt);

            foreach (var m in batch.Measurements)
            {
                pBssid.Value = m.Bssid;
                pSsid.Value = m.Ssid;
                pRssi.Value = m.RssiDbm;
                pQuality.Value = m.SignalQuality;
                pChannel.Value = m.Channel;
                pFreq.Value = m.FrequencyKHz;
                pTs.Value = m.TimestampMs;
                await measurementCmd.ExecuteNonQueryAsync(ct);

                apBssid.Value = m.Bssid;
                apSsid.Value = m.Ssid;
                apRssi.Value = m.RssiDbm;
                apQuality.Value = m.SignalQuality;
                apChannel.Value = m.Channel;
                apFreq.Value = m.FrequencyKHz;
                apBand.Value = WiFiNetwork.FrequencyToBand(m.FrequencyKHz);
                apTs.Value = m.TimestampMs;
                await apCmd.ExecuteNonQueryAsync(ct);
            }

            await transaction.CommitAsync(ct);
            _logger.LogDebug("Stored scan batch {BatchId}: Measurements={Count}", batch.BatchId, batch.Measurements.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to store batch to SQL Server.");
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<IReadOnlyList<SignalMeasurement>> GetMeasurementsAsync(string bssid, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default)
    {
        if (_connection == null) return Array.Empty<SignalMeasurement>();

        var results = new List<SignalMeasurement>();
        await _lock.WaitAsync(ct);
        try
        {
            await using var cmd = _connection.CreateCommand();
            cmd.CommandText = @"
                SELECT Bssid, Ssid, RssiDbm, SignalQuality, Channel, FrequencyKHz, TimestampMs
                FROM dbo.Measurements
                WHERE Bssid = @bssid AND TimestampMs >= @from AND TimestampMs <= @to
                ORDER BY TimestampMs ASC;";
            cmd.Parameters.AddWithValue("@bssid", bssid);
            cmd.Parameters.AddWithValue("@from", from.ToUnixTimeMilliseconds());
            cmd.Parameters.AddWithValue("@to", to.ToUnixTimeMilliseconds());

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                results.Add(ReadMeasurement(reader));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to query measurements. Bssid={Bssid}", bssid);
        }
        finally
        {
            _lock.Release();
        }
        return results;
    }

    public async Task<IReadOnlyList<SignalMeasurement>> GetLatestMeasurementsAsync(int maxAge_seconds = 60, CancellationToken ct = default)
    {
        if (_connection == null) return Array.Empty<SignalMeasurement>();

        var results = new List<SignalMeasurement>();
        var minTime = DateTimeOffset.UtcNow.AddSeconds(-maxAge_seconds).ToUnixTimeMilliseconds();

        await _lock.WaitAsync(ct);
        try
        {
            await using var cmd = _connection.CreateCommand();
            cmd.CommandText = @"
                SELECT Bssid, Ssid, RssiDbm, SignalQuality, Channel, FrequencyKHz, TimestampMs
                FROM dbo.Measurements M1
                WHERE TimestampMs = (
                    SELECT MAX(TimestampMs) FROM dbo.Measurements M2 WHERE M1.Bssid = M2.Bssid
                )
                AND TimestampMs >= @minTime;";
            cmd.Parameters.AddWithValue("@minTime", minTime);

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                results.Add(ReadMeasurement(reader));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to query latest measurements.");
        }
        finally
        {
            _lock.Release();
        }
        return results;
    }

    public async Task<IReadOnlyList<AccessPointDetails>> GetSavedAccessPointsAsync(int maxAge_seconds = 0, CancellationToken ct = default)
    {
        if (_connection == null) return Array.Empty<AccessPointDetails>();

        var results = new List<AccessPointDetails>();
        long minTime = maxAge_seconds > 0
            ? DateTimeOffset.UtcNow.AddSeconds(-maxAge_seconds).ToUnixTimeMilliseconds()
            : 0;

        await _lock.WaitAsync(ct);
        try
        {
            await using var cmd = _connection.CreateCommand();
            cmd.CommandText = @"
                SELECT Bssid, Ssid, LatestRssiDbm, SignalQuality, Channel, FrequencyKHz, Band,
                       FirstSeenMs, LastSeenMs, MinRssiDbm, MaxRssiDbm, SampleCount
                FROM dbo.AccessPoints
                WHERE LastSeenMs >= @minTime
                ORDER BY LastSeenMs DESC;";
            cmd.Parameters.AddWithValue("@minTime", minTime);

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                results.Add(new AccessPointDetails
                {
                    Bssid = reader.GetString(0),
                    Ssid = reader.GetString(1),
                    LatestRssiDbm = reader.GetInt32(2),
                    SignalQuality = reader.GetInt32(3),
                    Channel = reader.GetInt32(4),
                    FrequencyKHz = reader.GetInt32(5),
                    Band = reader.GetString(6),
                    FirstSeen = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(7)),
                    LastSeen = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(8)),
                    MinRssiDbm = reader.GetInt32(9),
                    MaxRssiDbm = reader.GetInt32(10),
                    SampleCount = reader.GetInt64(11)
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to query saved access points.");
        }
        finally
        {
            _lock.Release();
        }
        return results;
    }

    public async Task<IReadOnlyList<string>> GetActiveBssidsAsync(int maxAge_seconds = 300, CancellationToken ct = default)
    {
        if (_connection == null) return Array.Empty<string>();

        var results = new List<string>();
        var minTime = DateTimeOffset.UtcNow.AddSeconds(-maxAge_seconds).ToUnixTimeMilliseconds();

        await _lock.WaitAsync(ct);
        try
        {
            await using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT DISTINCT Bssid FROM dbo.Measurements WHERE TimestampMs >= @minTime;";
            cmd.Parameters.AddWithValue("@minTime", minTime);

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                results.Add(reader.GetString(0));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to query active BSSIDs.");
        }
        finally
        {
            _lock.Release();
        }
        return results;
    }

    public async Task PurgeOldDataAsync(TimeSpan retention, CancellationToken ct = default)
    {
        var hours = Math.Max(1, (int)retention.TotalHours);
        await PurgeNonTrainingDataAsync(hours, hours, hours, accessPointMaxAgeDays: 0, dryRun: false, ct);
    }

    public async Task<DataPurgeResult> PurgeNonTrainingDataAsync(
        int trainingRetentionHours = 168,
        int rawRetentionHours = 24,
        int replayRetentionHours = 48,
        int accessPointMaxAgeDays = 30,
        bool dryRun = false,
        CancellationToken ct = default)
    {
        if (_connection == null)
            return new DataPurgeResult { DryRun = dryRun };

        await _lock.WaitAsync(ct);
        try
        {
            await DeployPurgeProceduresAsync(ct);

            await using var cmd = _connection.CreateCommand();
            cmd.CommandType = System.Data.CommandType.StoredProcedure;
            cmd.CommandText = "dbo.PurgeNonTrainingData";
            cmd.Parameters.AddWithValue("@TrainingRetentionHours", trainingRetentionHours);
            cmd.Parameters.AddWithValue("@RawRetentionHours", rawRetentionHours);
            cmd.Parameters.AddWithValue("@ReplayRetentionHours", replayRetentionHours);
            cmd.Parameters.AddWithValue("@AccessPointMaxAgeDays", accessPointMaxAgeDays);
            cmd.Parameters.AddWithValue("@BatchSize", 50_000);
            cmd.Parameters.AddWithValue("@DryRun", dryRun);

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
                return new DataPurgeResult { DryRun = dryRun };

            var result = new DataPurgeResult
            {
                MeasurementsDeleted = reader.GetInt64(reader.GetOrdinal(
                    dryRun ? "MeasurementsToDelete" : "MeasurementsDeleted")),
                MotionEventsDeleted = reader.GetInt64(reader.GetOrdinal(
                    dryRun ? "MotionEventsToDelete" : "MotionEventsDeleted")),
                EnvironmentBatchesDeleted = reader.GetInt64(reader.GetOrdinal(
                    dryRun ? "EnvironmentBatchesToDelete" : "EnvironmentBatchesDeleted")),
                WiFiFeaturesDeleted = reader.GetInt64(reader.GetOrdinal(
                    dryRun ? "WiFiFeaturesToDelete" : "WiFiFeaturesDeleted")),
                ZoneStateDeleted = reader.GetInt64(reader.GetOrdinal(
                    dryRun ? "ZoneStateToDelete" : "ZoneStateDeleted")),
                AccessPointsDeleted = reader.GetInt64(reader.GetOrdinal(
                    dryRun ? "AccessPointsToDelete" : "AccessPointsDeleted")),
                DryRun = dryRun
            };

            _logger.LogInformation(
                "{Mode} purge (training={Training}h raw={Raw}h replay={Replay}h): Measurements={Meas}, Features={Feat}, Zones={Zones}, Events={Events}, EnvBatches={Env}, AccessPoints={Ap}.",
                dryRun ? "Dry-run" : "Applied",
                trainingRetentionHours,
                rawRetentionHours,
                replayRetentionHours,
                result.MeasurementsDeleted,
                result.WiFiFeaturesDeleted,
                result.ZoneStateDeleted,
                result.MotionEventsDeleted,
                result.EnvironmentBatchesDeleted,
                result.AccessPointsDeleted);

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to purge non-training data.");
            return new DataPurgeResult { DryRun = dryRun };
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task DeployPurgeProceduresAsync(CancellationToken ct)
    {
        if (_connection == null) return;

        var sqlPath = Path.Combine(AppContext.BaseDirectory, "sql", "PurgeNonTrainingData.sql");
        if (!File.Exists(sqlPath))
        {
            _logger.LogDebug("Purge SQL script not found at {Path}; skipping procedure deploy.", sqlPath);
            return;
        }

        var script = await File.ReadAllTextAsync(sqlPath, ct);
        var batches = script.Split(new[] { "\r\nGO\r\n", "\nGO\n", "\r\nGO\n", "\nGO\r\n" },
            StringSplitOptions.RemoveEmptyEntries);

        foreach (var batch in batches)
        {
            var trimmed = batch.Trim();
            if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith("/*", StringComparison.Ordinal))
                continue;
            if (trimmed.Equals("SET NOCOUNT ON;", StringComparison.OrdinalIgnoreCase))
                continue;

            await using var cmd = _connection.CreateCommand();
            cmd.CommandText = trimmed;
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    public async Task StoreFeaturesAsync(IReadOnlyList<WiFiFeatureRecord> features, CancellationToken ct = default)
    {
        if (_connection == null || features.Count == 0) return;

        await _lock.WaitAsync(ct);
        try
        {
            await using var cmd = _connection.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO dbo.WiFi_Features
                    (BatchId, Bssid, Ssid, TimestampMs, RawRssi, SmoothedRssi, Variance, StdDev,
                     ChangeRate, Entropy, CrossCorrelation, DominantFrequency, SpectralEnergy,
                     ZScore, IsAnomaly, MotionConfidence)
                VALUES
                    (@batch, @bssid, @ssid, @ts, @raw, @smooth, @var, @std, @rate, @ent, @corr,
                     @freq, @energy, @z, @anomaly, @motion);";

            var pBatch = cmd.Parameters.Add("@batch", System.Data.SqlDbType.NVarChar, 64);
            var pBssid = cmd.Parameters.Add("@bssid", System.Data.SqlDbType.NVarChar, 64);
            var pSsid = cmd.Parameters.Add("@ssid", System.Data.SqlDbType.NVarChar, 256);
            var pTs = cmd.Parameters.Add("@ts", System.Data.SqlDbType.BigInt);
            var pRaw = cmd.Parameters.Add("@raw", System.Data.SqlDbType.Int);
            var pSmooth = cmd.Parameters.Add("@smooth", System.Data.SqlDbType.Float);
            var pVar = cmd.Parameters.Add("@var", System.Data.SqlDbType.Float);
            var pStd = cmd.Parameters.Add("@std", System.Data.SqlDbType.Float);
            var pRate = cmd.Parameters.Add("@rate", System.Data.SqlDbType.Float);
            var pEnt = cmd.Parameters.Add("@ent", System.Data.SqlDbType.Float);
            var pCorr = cmd.Parameters.Add("@corr", System.Data.SqlDbType.Float);
            var pFreq = cmd.Parameters.Add("@freq", System.Data.SqlDbType.Float);
            var pEnergy = cmd.Parameters.Add("@energy", System.Data.SqlDbType.Float);
            var pZ = cmd.Parameters.Add("@z", System.Data.SqlDbType.Float);
            var pAnomaly = cmd.Parameters.Add("@anomaly", System.Data.SqlDbType.Bit);
            var pMotion = cmd.Parameters.Add("@motion", System.Data.SqlDbType.Float);

            foreach (var f in features)
            {
                pBatch.Value = f.BatchId;
                pBssid.Value = f.Bssid;
                pSsid.Value = f.Ssid;
                pTs.Value = f.TimestampMs;
                pRaw.Value = f.RawRssi;
                pSmooth.Value = f.SmoothedRssi;
                pVar.Value = f.Variance;
                pStd.Value = f.StdDev;
                pRate.Value = f.ChangeRate;
                pEnt.Value = f.Entropy;
                pCorr.Value = f.CrossCorrelation;
                pFreq.Value = f.DominantFrequency;
                pEnergy.Value = f.SpectralEnergy;
                pZ.Value = f.ZScore;
                pAnomaly.Value = f.IsAnomaly;
                pMotion.Value = f.MotionConfidence;
                await cmd.ExecuteNonQueryAsync(ct);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to store WiFi features.");
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task StoreMotionEventsAsync(string batchId, long timestampMs, IReadOnlyList<MotionEvent> events, CancellationToken ct = default)
    {
        if (_connection == null || events.Count == 0) return;

        await _lock.WaitAsync(ct);
        try
        {
            await using var cmd = _connection.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO dbo.Motion_Events
                    (EventId, BatchId, TimestampMs, EventType, Confidence, Description,
                     AffectedApCount, AffectedBssids, AverageVariance, PeakRssiChange, DurationMs)
                VALUES
                    (@eid, @batch, @ts, @type, @conf, @desc, @apCount, @bssids, @avgVar, @peak, @dur);";

            var pEid = cmd.Parameters.Add("@eid", System.Data.SqlDbType.UniqueIdentifier);
            var pBatch = cmd.Parameters.Add("@batch", System.Data.SqlDbType.NVarChar, 64);
            var pTs = cmd.Parameters.Add("@ts", System.Data.SqlDbType.BigInt);
            var pType = cmd.Parameters.Add("@type", System.Data.SqlDbType.Int);
            var pConf = cmd.Parameters.Add("@conf", System.Data.SqlDbType.Float);
            var pDesc = cmd.Parameters.Add("@desc", System.Data.SqlDbType.NVarChar, 512);
            var pApCount = cmd.Parameters.Add("@apCount", System.Data.SqlDbType.Int);
            var pBssids = cmd.Parameters.Add("@bssids", System.Data.SqlDbType.NVarChar, -1);
            var pAvgVar = cmd.Parameters.Add("@avgVar", System.Data.SqlDbType.Float);
            var pPeak = cmd.Parameters.Add("@peak", System.Data.SqlDbType.Float);
            var pDur = cmd.Parameters.Add("@dur", System.Data.SqlDbType.Int);

            foreach (var e in events)
            {
                pEid.Value = e.Id;
                pBatch.Value = batchId;
                pTs.Value = e.Timestamp.ToUnixTimeMilliseconds();
                pType.Value = (int)e.EventType;
                pConf.Value = e.Confidence;
                pDesc.Value = e.Description;
                pApCount.Value = e.AffectedApCount;
                pBssids.Value = string.Join(",", e.AffectedBssids);
                pAvgVar.Value = e.AverageVariance;
                pPeak.Value = e.PeakRssiChange;
                pDur.Value = e.DurationMs;
                await cmd.ExecuteNonQueryAsync(ct);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to store motion events.");
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task StoreZoneStatesAsync(string batchId, long timestampMs, IReadOnlyList<SpatialZone> zones, CancellationToken ct = default)
    {
        if (_connection == null || zones.Count == 0) return;

        await _lock.WaitAsync(ct);
        try
        {
            await using var cmd = _connection.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO dbo.Zone_State
                    (BatchId, TimestampMs, ZoneId, Name, X, Y, Radius, OccupancyConfidence, MotionConfidence, Color)
                VALUES
                    (@batch, @ts, @zid, @name, @x, @y, @radius, @occ, @motion, @color);";

            var pBatch = cmd.Parameters.Add("@batch", System.Data.SqlDbType.NVarChar, 64);
            var pTs = cmd.Parameters.Add("@ts", System.Data.SqlDbType.BigInt);
            var pZid = cmd.Parameters.Add("@zid", System.Data.SqlDbType.NVarChar, 64);
            var pName = cmd.Parameters.Add("@name", System.Data.SqlDbType.NVarChar, 128);
            var pX = cmd.Parameters.Add("@x", System.Data.SqlDbType.Float);
            var pY = cmd.Parameters.Add("@y", System.Data.SqlDbType.Float);
            var pRadius = cmd.Parameters.Add("@radius", System.Data.SqlDbType.Float);
            var pOcc = cmd.Parameters.Add("@occ", System.Data.SqlDbType.Float);
            var pMotion = cmd.Parameters.Add("@motion", System.Data.SqlDbType.Float);
            var pColor = cmd.Parameters.Add("@color", System.Data.SqlDbType.NVarChar, 16);

            foreach (var z in zones)
            {
                pBatch.Value = batchId;
                pTs.Value = timestampMs;
                pZid.Value = z.ZoneId;
                pName.Value = z.Name;
                pX.Value = z.X;
                pY.Value = z.Y;
                pRadius.Value = z.Radius;
                pOcc.Value = z.OccupancyConfidence;
                pMotion.Value = z.MotionConfidence;
                pColor.Value = z.Color;
                await cmd.ExecuteNonQueryAsync(ct);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to store zone states.");
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task StoreEnvironmentBatchAsync(EnvironmentBatchRecord batch, CancellationToken ct = default)
    {
        if (_connection == null) return;

        await _lock.WaitAsync(ct);
        try
        {
            await using var cmd = _connection.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO dbo.Environment_Batches
                    (BatchId, TimestampMs, MotionConfidence, OccupancyConfidence, Classification,
                     StabilityIndex, LstmMotionConfidence, CnnActivityScore, ActiveApCount, Source)
                VALUES
                    (@batch, @ts, @motion, @occ, @class, @stab, @lstm, @cnn, @apCount, @source);";
            cmd.Parameters.AddWithValue("@batch", batch.BatchId);
            cmd.Parameters.AddWithValue("@ts", batch.TimestampMs);
            cmd.Parameters.AddWithValue("@motion", batch.MotionConfidence);
            cmd.Parameters.AddWithValue("@occ", batch.OccupancyConfidence);
            cmd.Parameters.AddWithValue("@class", batch.Classification);
            cmd.Parameters.AddWithValue("@stab", batch.StabilityIndex);
            cmd.Parameters.AddWithValue("@lstm", batch.LstmMotionConfidence);
            cmd.Parameters.AddWithValue("@cnn", batch.CnnActivityScore);
            cmd.Parameters.AddWithValue("@apCount", batch.ActiveApCount);
            cmd.Parameters.AddWithValue("@source", batch.Source);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to store environment batch {BatchId}.", batch.BatchId);
        }
        finally
        {
            _lock.Release();
        }
    }

    private static SignalMeasurement ReadMeasurement(SqlDataReader reader) => new()
    {
        Bssid = reader.GetString(0),
        Ssid = reader.GetString(1),
        RssiDbm = reader.GetInt32(2),
        SignalQuality = reader.GetInt32(3),
        Channel = reader.GetInt32(4),
        FrequencyKHz = reader.GetInt32(5),
        Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(6))
    };

    public void Dispose()
    {
        _connection?.Dispose();
        _lock.Dispose();
    }
}
