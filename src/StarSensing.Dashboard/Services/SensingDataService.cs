using Microsoft.Data.SqlClient;

using StarSensing.Core.Protos;



namespace StarSensing.Dashboard.Services;



public sealed class StoredMotionEvent

{

    public Guid EventId { get; init; }

    public DateTimeOffset Timestamp { get; init; }

    public int EventType { get; init; }

    public double Confidence { get; init; }

    public string Description { get; init; } = "";

    public int AffectedApCount { get; init; }

    public double AverageVariance { get; init; }

}



public sealed class StoredZone

{

    public string ZoneId { get; init; } = "";

    public string Name { get; init; } = "";

    public double X { get; init; }

    public double Y { get; init; }

    public double Radius { get; init; }

    public double OccupancyConfidence { get; init; }

    public double MotionConfidence { get; init; }

    public string Color { get; init; } = "#00f5d4";

    public DateTimeOffset Timestamp { get; init; }

}



public sealed class SpatialApFeature
{
    public string Bssid { get; init; } = "";
    public double Variance { get; init; }
    public double Entropy { get; init; }
    public double MotionConfidence { get; init; }
}

public sealed class ReplayFrame
{
    public string BatchId { get; init; } = "";
    public DateTimeOffset Timestamp { get; init; }
    public double MotionConfidence { get; init; }
    public double AvgVariance { get; init; }
    public double AvgEntropy { get; init; }
    public int ActiveApCount { get; init; }
    public string Classification { get; init; } = "STATIC";
    public double LstmMotionConfidence { get; init; }
    public double CnnActivityScore { get; init; }
    public double StabilityIndex { get; init; }
    public double OccupancyConfidence { get; init; }
    public IReadOnlyList<StoredZone> Zones { get; init; } = Array.Empty<StoredZone>();
    public IReadOnlyList<SpatialApFeature> ApFeatures { get; init; } = Array.Empty<SpatialApFeature>();
}



/// <summary>Reads persisted ML data from SQL Server (WiFi_Features, Motion_Events, Zone_State).</summary>

public sealed class SensingDataService

{

    private const string ConnectionString =

        "Server=localhost;Database=StarSensing;Trusted_Connection=True;TrustServerCertificate=True;Encrypt=False;";



    public async Task<IReadOnlyList<StoredMotionEvent>> GetRecentMotionEventsAsync(int limit = 50)

    {

        var list = new List<StoredMotionEvent>();

        try

        {

            await using var conn = new SqlConnection(ConnectionString);

            await conn.OpenAsync();

            await using var cmd = conn.CreateCommand();

            cmd.CommandText = $@"

                SELECT TOP ({limit}) EventId, TimestampMs, EventType, Confidence, Description,

                       AffectedApCount, AverageVariance

                FROM dbo.Motion_Events ORDER BY TimestampMs DESC;";

            await using var r = await cmd.ExecuteReaderAsync();

            while (await r.ReadAsync())

            {

                list.Add(new StoredMotionEvent

                {

                    EventId = r.GetGuid(0),

                    Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(1)),

                    EventType = r.GetInt32(2),

                    Confidence = r.GetDouble(3),

                    Description = r.GetString(4),

                    AffectedApCount = r.GetInt32(5),

                    AverageVariance = r.GetDouble(6)

                });

            }

        }

        catch { }

        return list;

    }



    public async Task<IReadOnlyList<StoredZone>> GetLatestZonesAsync(int limit = 20)

    {

        var list = new List<StoredZone>();

        try

        {

            await using var conn = new SqlConnection(ConnectionString);

            await conn.OpenAsync();

            await using var cmd = conn.CreateCommand();

            cmd.CommandText = $@"

                WITH Latest AS (

                    SELECT MAX(TimestampMs) AS Ts FROM dbo.Zone_State

                )

                SELECT TOP ({limit}) ZoneId, Name, X, Y, Radius,

                       OccupancyConfidence, MotionConfidence, Color, TimestampMs

                FROM dbo.Zone_State

                WHERE TimestampMs = (SELECT Ts FROM Latest)

                ORDER BY ZoneId;";

            await using var r = await cmd.ExecuteReaderAsync();

            while (await r.ReadAsync())

            {

                list.Add(ReadZone(r));

            }

        }

        catch { }

        return list;

    }



    /// <summary>Batch-aggregated timeline for motion/spatial replay scrubber.</summary>
    public Task<IReadOnlyList<ReplayFrame>> GetReplayTimelineAsync(int minutesBack = 30, int maxFrames = 500)
    {
        long fromMs = DateTimeOffset.UtcNow.AddMinutes(-minutesBack).ToUnixTimeMilliseconds();
        long toMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        return GetReplayTimelineAsync(fromMs, toMs, maxFrames);
    }

    public async Task<IReadOnlyList<ReplayFrame>> GetReplayTimelineAsync(long fromMs, long toMs, int maxFrames = 500)
    {
        var frames = new List<ReplayFrame>();
        try
        {
            await using var conn = new SqlConnection(ConnectionString);
            await conn.OpenAsync();

            var batchMeta = new Dictionary<string, (long Ts, double Motion, double Occ, int Class, double Stab, double Lstm, double Cnn, int ApCount, string Source)>(StringComparer.OrdinalIgnoreCase);
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = $@"
                    SELECT TOP ({maxFrames}) BatchId, TimestampMs, MotionConfidence, OccupancyConfidence,
                           Classification, StabilityIndex, LstmMotionConfidence, CnnActivityScore, ActiveApCount, Source
                    FROM dbo.Environment_Batches
                    WHERE TimestampMs >= @from AND TimestampMs <= @to
                    ORDER BY TimestampMs ASC;";
                cmd.Parameters.AddWithValue("@from", fromMs);
                cmd.Parameters.AddWithValue("@to", toMs);
                await using var r = await cmd.ExecuteReaderAsync();
                while (await r.ReadAsync())
                {
                    string batchId = r.GetString(0);
                    batchMeta[batchId] = (
                        r.GetInt64(1),
                        r.GetDouble(2),
                        r.GetDouble(3),
                        r.GetInt32(4),
                        r.GetDouble(5),
                        r.GetDouble(6),
                        r.GetDouble(7),
                        r.GetInt32(8),
                        r.IsDBNull(9) ? "Fallback" : r.GetString(9));
                }
            }

            var apByBatch = new Dictionary<string, List<SpatialApFeature>>(StringComparer.OrdinalIgnoreCase);
            var batchStats = new Dictionary<string, (long Ts, double AvgVar, double MaxMotion, double AvgEnt, int Count)>(StringComparer.OrdinalIgnoreCase);
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = $@"
                    SELECT BatchId, TimestampMs, Bssid, Variance, MotionConfidence, Entropy
                    FROM dbo.WiFi_Features
                    WHERE TimestampMs >= @from AND TimestampMs <= @to
                    ORDER BY TimestampMs ASC;";
                cmd.Parameters.AddWithValue("@from", fromMs);
                cmd.Parameters.AddWithValue("@to", toMs);
                await using var r = await cmd.ExecuteReaderAsync();
                while (await r.ReadAsync())
                {
                    string batch = r.GetString(0);
                    long ts = r.GetInt64(1);
                    string bssid = r.GetString(2);
                    double variance = r.GetDouble(3);
                    double motion = r.GetDouble(4);
                    double ent = r.GetDouble(5);

                    if (!apByBatch.TryGetValue(batch, out var apList))
                    {
                        apList = new List<SpatialApFeature>();
                        apByBatch[batch] = apList;
                    }
                    apList.Add(new SpatialApFeature
                    {
                        Bssid = bssid,
                        Variance = variance,
                        Entropy = ent,
                        MotionConfidence = motion
                    });

                    if (!batchStats.TryGetValue(batch, out var agg))
                        batchStats[batch] = (ts, variance, motion, ent, 1);
                    else
                    {
                        int n = agg.Count + 1;
                        batchStats[batch] = (
                            Math.Min(agg.Ts, ts),
                            (agg.AvgVar * agg.Count + variance) / n,
                            Math.Max(agg.MaxMotion, motion),
                            (agg.AvgEnt * agg.Count + ent) / n,
                            n);
                    }
                }
            }

            var zonesByBatch = new Dictionary<string, List<StoredZone>>(StringComparer.OrdinalIgnoreCase);
            var zoneSnapshots = new List<StoredZone>();
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"
                    SELECT BatchId, ZoneId, Name, X, Y, Radius, OccupancyConfidence, MotionConfidence, Color, TimestampMs
                    FROM dbo.Zone_State
                    WHERE TimestampMs >= @from AND TimestampMs <= @to
                    ORDER BY TimestampMs ASC;";
                cmd.Parameters.AddWithValue("@from", fromMs);
                cmd.Parameters.AddWithValue("@to", toMs);
                await using var r = await cmd.ExecuteReaderAsync();
                while (await r.ReadAsync())
                {
                    string? batchId = r.IsDBNull(0) ? null : r.GetString(0);
                    var zone = new StoredZone
                    {
                        ZoneId = r.GetString(1),
                        Name = r.GetString(2),
                        X = r.GetDouble(3),
                        Y = r.GetDouble(4),
                        Radius = r.GetDouble(5),
                        OccupancyConfidence = r.GetDouble(6),
                        MotionConfidence = r.GetDouble(7),
                        Color = r.GetString(8),
                        Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(9))
                    };
                    zoneSnapshots.Add(zone);
                    if (!string.IsNullOrEmpty(batchId))
                    {
                        if (!zonesByBatch.TryGetValue(batchId, out var zlist))
                        {
                            zlist = new List<StoredZone>();
                            zonesByBatch[batchId] = zlist;
                        }
                        zlist.Add(zone);
                    }
                }
            }

            IEnumerable<string> batchIds = batchMeta.Count > 0
                ? batchMeta.Keys.OrderBy(k => batchMeta[k].Ts)
                : batchStats.Keys.OrderBy(k => batchStats[k].Ts);

            foreach (var batchId in batchIds.Take(maxFrames))
            {
                batchStats.TryGetValue(batchId, out var agg);
                var hasMeta = batchMeta.TryGetValue(batchId, out var meta);

                long ts = hasMeta && meta.Ts != 0 ? meta.Ts : agg.Ts;
                double motion = hasMeta
                    ? meta.Motion
                    : (agg.MaxMotion > 0 ? agg.MaxMotion : Math.Min(1.0, agg.AvgVar / 6.0));

                var zones = zonesByBatch.TryGetValue(batchId, out var zb)
                    ? zb
                    : zoneSnapshots.Where(z => Math.Abs(z.Timestamp.ToUnixTimeMilliseconds() - ts) < 2500).ToList();

                apByBatch.TryGetValue(batchId, out var apFeatures);

                frames.Add(new ReplayFrame
                {
                    BatchId = batchId,
                    Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(ts),
                    MotionConfidence = motion,
                    AvgVariance = agg.AvgVar,
                    AvgEntropy = agg.AvgEnt,
                    ActiveApCount = hasMeta && meta.ApCount > 0 ? meta.ApCount : agg.Count,
                    Classification = hasMeta ? ClassifyFromInt(meta.Class) : Classify(motion),
                    LstmMotionConfidence = hasMeta ? meta.Lstm : 0,
                    CnnActivityScore = hasMeta ? meta.Cnn : 0,
                    StabilityIndex = hasMeta ? meta.Stab : 0,
                    OccupancyConfidence = hasMeta ? meta.Occ : 0,
                    Zones = zones,
                    ApFeatures = (IReadOnlyList<SpatialApFeature>)(apFeatures ?? new List<SpatialApFeature>())
                });
            }
        }
        catch { }

        return frames;
    }

    /// <summary>Channel waterfall points sorted by time for historical channel mode.</summary>
    public async Task<IReadOnlyList<(long TimestampMs, int Channel, double Rssi)>> GetChannelHistoryAsync(
        long fromMs, long toMs, int maxPoints = 5000)
    {
        var list = new List<(long, int, double)>();
        try
        {
            await using var conn = new SqlConnection(ConnectionString);
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $@"
                SELECT TOP ({maxPoints}) TimestampMs, Channel, RssiDbm
                FROM dbo.Measurements
                WHERE TimestampMs >= @from AND TimestampMs <= @to AND Channel BETWEEN 1 AND 14
                ORDER BY TimestampMs ASC;";
            cmd.Parameters.AddWithValue("@from", fromMs);
            cmd.Parameters.AddWithValue("@to", toMs);
            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
                list.Add((r.GetInt64(0), r.GetInt32(1), r.GetDouble(2)));
        }
        catch { }
        return list;
    }



    private static string Classify(double motion) => ClassifyFromInt(ClassifyToInt(motion));

    private static int ClassifyToInt(double motion) => motion switch
    {
        >= 0.8 => 3,
        >= 0.5 => 2,
        >= 0.2 => 1,
        > 0.05 => 4,
        _ => 0
    };

    private static string ClassifyFromInt(int classification) => classification switch
    {
        3 => "HIGH ACTIVITY",
        2 => "MODERATE ACTIVITY",
        1 => "LOW ACTIVITY",
        4 => "TRANSITION",
        _ => "STATIC"
    };



    private static StoredZone ReadZone(SqlDataReader r) => new()
    {
        ZoneId = r.GetString(0),
        Name = r.GetString(1),
        X = r.GetDouble(2),
        Y = r.GetDouble(3),
        Radius = r.GetDouble(4),
        OccupancyConfidence = r.GetDouble(5),
        MotionConfidence = r.GetDouble(6),
        Color = r.GetString(7),
        Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(8))
    };



    public static MotionEventMsg ToProto(StoredMotionEvent e) => new()

    {

        EventId = e.EventId.ToString(),

        Timestamp = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(e.Timestamp),

        EventType = (MotionEventTypeMsg)Math.Clamp(e.EventType, 0, 7),

        Confidence = e.Confidence,

        Description = e.Description,

        AffectedApCount = e.AffectedApCount,

        AverageVariance = e.AverageVariance

    };



    public static SpatialZoneMsg ToProto(StoredZone z) => new()

    {

        ZoneId = z.ZoneId,

        Name = z.Name,

        X = z.X,

        Y = z.Y,

        Radius = z.Radius,

        OccupancyConfidence = z.OccupancyConfidence,

        MotionConfidence = z.MotionConfidence,

        Color = z.Color

    };

}


