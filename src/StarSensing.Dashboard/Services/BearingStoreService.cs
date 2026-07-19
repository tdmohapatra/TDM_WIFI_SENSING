using Microsoft.Data.SqlClient;

namespace StarSensing.Dashboard.Services;

/// <summary>
/// Persistent per-router bearing (compass degrees: 0=N, 90=E, clockwise).
/// Priority: manual calibration > saved location snapshot > caller fallback.
/// </summary>
public sealed class BearingStoreService
{
    private const string ConnectionString =
        "Server=localhost;Database=StarSensing;Trusted_Connection=True;TrustServerCertificate=True;Encrypt=False;";

    private readonly Dictionary<string, (double Deg, string Source)> _cache = new(StringComparer.OrdinalIgnoreCase);
    private double _northOffsetDeg;
    private bool _initialized;

    public event Action? BearingsChanged;

    public double NorthOffsetDeg => _northOffsetDeg;

    public async Task InitializeAsync()
    {
        if (_initialized) return;
        await EnsureSchemaAsync();
        await ReloadAsync();
        _initialized = true;
    }

    private async Task EnsureSchemaAsync()
    {
        await using var conn = new SqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            IF OBJECT_ID(N'dbo.RouterBearings', N'U') IS NULL
            CREATE TABLE dbo.RouterBearings (
                Bssid       NVARCHAR(64)  NOT NULL PRIMARY KEY,
                BearingDeg  FLOAT         NOT NULL,
                Source      NVARCHAR(32)  NOT NULL,
                UpdatedAt   DATETIME2     NOT NULL
            );

            IF OBJECT_ID(N'dbo.MapSettings', N'U') IS NULL
            CREATE TABLE dbo.MapSettings (
                SettingKey   NVARCHAR(64) NOT NULL PRIMARY KEY,
                SettingValue FLOAT        NOT NULL
            );";
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task ReloadAsync()
    {
        _cache.Clear();
        await using var conn = new SqlConnection(ConnectionString);
        await conn.OpenAsync();

        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT Bssid, BearingDeg, Source FROM dbo.RouterBearings;";
            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
                _cache[r.GetString(0)] = (r.GetDouble(1), r.GetString(2));
        }

        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT SettingValue FROM dbo.MapSettings WHERE SettingKey = N'NorthOffsetDeg';";
            var val = await cmd.ExecuteScalarAsync();
            _northOffsetDeg = val is double d ? d : 0;
        }

        BearingsChanged?.Invoke();
    }

    public double GetBearing(string bssid, double fallbackDeg)
    {
        if (_cache.TryGetValue(bssid, out var entry))
            return NormalizeDeg(entry.Deg);
        return NormalizeDeg(fallbackDeg);
    }

    public bool TryGetBearing(string bssid, out double deg, out string source)
    {
        if (_cache.TryGetValue(bssid, out var entry))
        {
            deg = NormalizeDeg(entry.Deg);
            source = entry.Source;
            return true;
        }

        deg = 0;
        source = "";
        return false;
    }

    public IReadOnlyDictionary<string, double> GetAllBearings()
    {
        return _cache.ToDictionary(kv => kv.Key, kv => NormalizeDeg(kv.Value.Deg), StringComparer.OrdinalIgnoreCase);
    }

    public async Task SetBearingAsync(string bssid, double bearingDeg, string source = "manual")
    {
        bearingDeg = NormalizeDeg(bearingDeg);
        _cache[bssid] = (bearingDeg, source);

        await using var conn = new SqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            MERGE dbo.RouterBearings AS t
            USING (SELECT @bssid AS Bssid) AS s ON t.Bssid = s.Bssid
            WHEN MATCHED THEN UPDATE SET BearingDeg = @deg, Source = @src, UpdatedAt = @at
            WHEN NOT MATCHED THEN INSERT (Bssid, BearingDeg, Source, UpdatedAt)
                VALUES (@bssid, @deg, @src, @at);";
        cmd.Parameters.AddWithValue("@bssid", bssid);
        cmd.Parameters.AddWithValue("@deg", bearingDeg);
        cmd.Parameters.AddWithValue("@src", source);
        cmd.Parameters.AddWithValue("@at", DateTime.UtcNow);
        await cmd.ExecuteNonQueryAsync();

        BearingsChanged?.Invoke();
    }

    public async Task SetNorthOffsetAsync(double offsetDeg)
    {
        _northOffsetDeg = NormalizeDeg(offsetDeg);
        await using var conn = new SqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            MERGE dbo.MapSettings AS t
            USING (SELECT N'NorthOffsetDeg' AS SettingKey) AS s ON t.SettingKey = s.SettingKey
            WHEN MATCHED THEN UPDATE SET SettingValue = @val
            WHEN NOT MATCHED THEN INSERT (SettingKey, SettingValue) VALUES (N'NorthOffsetDeg', @val);";
        cmd.Parameters.AddWithValue("@val", _northOffsetDeg);
        await cmd.ExecuteNonQueryAsync();
        BearingsChanged?.Invoke();
    }

    /// <summary>Compass heading (0=N) adjusted by map north offset → map bearing.</summary>
    public double CompassToMapBearing(double compassHeadingDeg) =>
        NormalizeDeg(compassHeadingDeg - _northOffsetDeg);

    public static double NormalizeDeg(double deg)
    {
        deg %= 360.0;
        if (deg < 0) deg += 360.0;
        return deg;
    }

    /// <summary>Compass convention: 0=N, 90=E. Returns ground-plane metres (east, north).</summary>
    public static (double East, double North) PolarToMeters(double bearingDeg, double distanceMeters)
    {
        double rad = bearingDeg * Math.PI / 180.0;
        return (distanceMeters * Math.Sin(rad), distanceMeters * Math.Cos(rad));
    }

    public static (double X, double Y) MetersPolarToNormalized(double bearingDeg, double distanceMeters, double mapRadiusMeters = 30.0)
    {
        var (east, north) = PolarToMeters(bearingDeg, Math.Max(0.05, distanceMeters));
        double span = Math.Max(5.0, mapRadiusMeters) * 2.0;
        return (Math.Clamp(0.5 + east / span, 0.0, 1.0), Math.Clamp(0.5 + north / span, 0.0, 1.0));
    }
}
