using Microsoft.Data.SqlClient;
using StarSensing.Dashboard.Models;

namespace StarSensing.Dashboard.Services;

/// <summary>
/// Persists user-defined locations and the Wi-Fi signals captured at each one
/// into the local SQL Server (same instance the Engine uses).
/// </summary>
public sealed class LocationStoreService
{
    private const string ConnectionString =
        "Server=localhost;Database=StarSensing;Trusted_Connection=True;TrustServerCertificate=True;Encrypt=False;";

    private bool _initialized;

    private async Task EnsureSchemaAsync()
    {
        if (_initialized) return;

        await using var conn = new SqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            IF OBJECT_ID(N'dbo.Locations', N'U') IS NULL
            CREATE TABLE dbo.Locations (
                LocationId   UNIQUEIDENTIFIER NOT NULL PRIMARY KEY,
                Name         NVARCHAR(256)    NOT NULL,
                Latitude     FLOAT            NULL,
                Longitude    FLOAT            NULL,
                SignalCount  INT              NOT NULL,
                CreatedAt    DATETIME2        NOT NULL
            );

            IF OBJECT_ID(N'dbo.LocationSignals', N'U') IS NULL
            CREATE TABLE dbo.LocationSignals (
                Id              BIGINT IDENTITY(1,1) PRIMARY KEY,
                LocationId      UNIQUEIDENTIFIER NOT NULL,
                Ssid            NVARCHAR(256) NOT NULL,
                Bssid           NVARCHAR(64)  NOT NULL,
                RssiDbm         INT           NOT NULL,
                SignalQuality   INT           NOT NULL,
                DistanceMeters  FLOAT         NOT NULL,
                DirectionDeg    FLOAT         NOT NULL,
                Band            NVARCHAR(16)  NULL,
                Channel         INT           NOT NULL,
                SavedAt         DATETIME2     NOT NULL
            );";
        await cmd.ExecuteNonQueryAsync();
        _initialized = true;
    }

    /// <summary>
    /// Saves the given networks under a new location record. Returns the new location id.
    /// </summary>
    public async Task<Guid> SaveLocationAsync(
        string name,
        double? latitude,
        double? longitude,
        IReadOnlyList<SelectableNetwork> networks)
    {
        await EnsureSchemaAsync();

        var locationId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        await using var conn = new SqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync();

        await using (var loc = conn.CreateCommand())
        {
            loc.Transaction = tx;
            loc.CommandText = @"
                INSERT INTO dbo.Locations (LocationId, Name, Latitude, Longitude, SignalCount, CreatedAt)
                VALUES (@id, @name, @lat, @lon, @count, @created);";
            loc.Parameters.AddWithValue("@id", locationId);
            loc.Parameters.AddWithValue("@name", name);
            loc.Parameters.AddWithValue("@lat", (object?)latitude ?? DBNull.Value);
            loc.Parameters.AddWithValue("@lon", (object?)longitude ?? DBNull.Value);
            loc.Parameters.AddWithValue("@count", networks.Count);
            loc.Parameters.AddWithValue("@created", now);
            await loc.ExecuteNonQueryAsync();
        }

        await using (var sig = conn.CreateCommand())
        {
            sig.Transaction = tx;
            sig.CommandText = @"
                INSERT INTO dbo.LocationSignals
                    (LocationId, Ssid, Bssid, RssiDbm, SignalQuality, DistanceMeters, DirectionDeg, Band, Channel, SavedAt)
                VALUES
                    (@loc, @ssid, @bssid, @rssi, @quality, @dist, @dir, @band, @channel, @saved);";

            var pLoc = sig.Parameters.Add("@loc", System.Data.SqlDbType.UniqueIdentifier);
            var pSsid = sig.Parameters.Add("@ssid", System.Data.SqlDbType.NVarChar, 256);
            var pBssid = sig.Parameters.Add("@bssid", System.Data.SqlDbType.NVarChar, 64);
            var pRssi = sig.Parameters.Add("@rssi", System.Data.SqlDbType.Int);
            var pQuality = sig.Parameters.Add("@quality", System.Data.SqlDbType.Int);
            var pDist = sig.Parameters.Add("@dist", System.Data.SqlDbType.Float);
            var pDir = sig.Parameters.Add("@dir", System.Data.SqlDbType.Float);
            var pBand = sig.Parameters.Add("@band", System.Data.SqlDbType.NVarChar, 16);
            var pChannel = sig.Parameters.Add("@channel", System.Data.SqlDbType.Int);
            var pSaved = sig.Parameters.Add("@saved", System.Data.SqlDbType.DateTime2);

            foreach (var n in networks)
            {
                pLoc.Value = locationId;
                pSsid.Value = n.Ssid;
                pBssid.Value = n.Bssid;
                pRssi.Value = n.LatestRssi;
                pQuality.Value = n.SignalQuality;
                pDist.Value = n.DistanceMeters;
                pDir.Value = n.BearingDegrees;
                pBand.Value = (object?)n.Band ?? DBNull.Value;
                pChannel.Value = n.Channel;
                pSaved.Value = now;
                await sig.ExecuteNonQueryAsync();
            }
        }

        await tx.CommitAsync();
        return locationId;
    }

    /// <summary>
    /// Returns stable bearing (degrees) per router from the most recent saved location.
    /// Live map radius always comes from current RSSI distance, not this snapshot.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, double>> GetRouterBearingsAsync()
    {
        var map = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        try
        {
            await EnsureSchemaAsync();
            await using var conn = new SqlConnection(ConnectionString);
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                WITH Latest AS (
                    SELECT TOP 1 LocationId FROM dbo.Locations ORDER BY CreatedAt DESC
                )
                SELECT Bssid, DirectionDeg
                FROM dbo.LocationSignals
                WHERE LocationId = (SELECT LocationId FROM Latest);";
            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
                map[r.GetString(0)] = r.GetDouble(1);
        }
        catch { }
        return map;
    }

    /// <summary>
    /// Normalized 0..1 floor coordinates from true meters (for heatmap floor plan).
    /// </summary>
    public async Task<IReadOnlyDictionary<string, (double X, double Y)>> GetRouterPositionsAsync(double mapRadiusMeters = 30.0)
    {
        var map = new Dictionary<string, (double X, double Y)>(StringComparer.OrdinalIgnoreCase);
        try
        {
            await EnsureSchemaAsync();
            await using var conn = new SqlConnection(ConnectionString);
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                WITH Latest AS (
                    SELECT TOP 1 LocationId FROM dbo.Locations ORDER BY CreatedAt DESC
                )
                SELECT Bssid, DirectionDeg, DistanceMeters
                FROM dbo.LocationSignals
                WHERE LocationId = (SELECT LocationId FROM Latest);";
            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
            {
                string bssid = r.GetString(0);
                double dir = r.GetDouble(1);
                double dist = r.GetDouble(2);
                map[bssid] = MetersPolarToNormalized(dir, dist, mapRadiusMeters);
            }
        }
        catch { }
        return map;
    }

    /// <summary>Maps ground-plane metres to normalized floor-plan coordinates (compass bearing).</summary>
    public static (double X, double Y) MetersPolarToNormalized(double bearingDeg, double distanceMeters, double mapRadiusMeters = 30.0) =>
        BearingStoreService.MetersPolarToNormalized(bearingDeg, distanceMeters, mapRadiusMeters);
}
