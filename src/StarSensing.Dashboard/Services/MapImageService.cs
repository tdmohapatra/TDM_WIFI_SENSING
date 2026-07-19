using System.Net.Http;
using System.Text.Json;

namespace StarSensing.Dashboard.Services;

/// <summary>
/// Fetches real satellite imagery (Esri World Imagery) for an approximate location
/// and an approximate viewport size in metres. Location is resolved by IP if not provided.
/// </summary>
public sealed class MapImageService
{
    private readonly HttpClient _http;

    public MapImageService()
    {
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        // Some providers (e.g. ipapi.co) return 403 without a browser User-Agent.
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) StarSensing/1.0");
    }

    /// <summary>Resolve an approximate (lat, lon) from the public IP address.</summary>
    public async Task<(double Lat, double Lon)?> GetIpLocationAsync()
    {
        // Primary: HTTPS provider (no key required).
        try
        {
            var json = await _http.GetStringAsync("https://ipapi.co/json/");
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("latitude", out var lat) &&
                root.TryGetProperty("longitude", out var lon) &&
                lat.TryGetDouble(out var latVal) &&
                lon.TryGetDouble(out var lonVal))
            {
                return (latVal, lonVal);
            }
        }
        catch
        {
            // Fall through to secondary provider.
        }

        // Fallback: ip-api (HTTP, free tier).
        try
        {
            var json = await _http.GetStringAsync("http://ip-api.com/json/?fields=status,lat,lon");
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("status", out var status) &&
                status.GetString() == "success" &&
                root.TryGetProperty("lat", out var lat) &&
                root.TryGetProperty("lon", out var lon))
            {
                return (lat.GetDouble(), lon.GetDouble());
            }
        }
        catch
        {
            // Network/permission failures are non-fatal; caller handles null.
        }

        return null;
    }

    /// <summary>
    /// Fetch a top-down satellite PNG roughly covering <paramref name="viewportMeters"/> across,
    /// centred on the given coordinate. Returns encoded PNG bytes or null on failure.
    /// </summary>
    public async Task<byte[]?> FetchSatelliteAsync(double lat, double lon, double viewportMeters, int sizePx = 640)
    {
        try
        {
            // Satellite imagery has limited resolution; below ~40 m a tile is just blurry
            // upscaling. Clamp the fetched span so real streets/buildings stay visible even
            // when the map is zoomed to centimetres.
            double span = Math.Max(viewportMeters, 40.0);

            // Half-extent in metres, with a small margin so the outer ring still has context.
            double half = span / 2.0 * 1.15;

            double dLat = half / 111320.0;
            double cosLat = Math.Cos(lat * Math.PI / 180.0);
            double dLon = half / (111320.0 * Math.Max(0.05, Math.Abs(cosLat)));

            double minLon = lon - dLon;
            double minLat = lat - dLat;
            double maxLon = lon + dLon;
            double maxLat = lat + dLat;

            string url =
                "https://server.arcgisonline.com/ArcGIS/rest/services/World_Imagery/MapServer/export" +
                $"?bbox={minLon},{minLat},{maxLon},{maxLat}" +
                "&bboxSR=4326&imageSR=4326" +
                $"&size={sizePx},{sizePx}" +
                "&format=png32&transparent=false&f=image";

            return await _http.GetByteArrayAsync(url);
        }
        catch
        {
            return null;
        }
    }
}
