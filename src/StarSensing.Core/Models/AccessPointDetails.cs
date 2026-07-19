namespace StarSensing.Core.Models;

/// <summary>
/// Persistent metadata and aggregate statistics for a Wi-Fi access point.
/// </summary>
public sealed class AccessPointDetails
{
    public string Bssid { get; set; } = string.Empty;
    public string Ssid { get; set; } = string.Empty;
    public int LatestRssiDbm { get; set; }
    public int SignalQuality { get; set; }
    public int FrequencyKHz { get; set; }
    public int Channel { get; set; }
    public string Band { get; set; } = string.Empty;
    public DateTimeOffset FirstSeen { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastSeen { get; set; } = DateTimeOffset.UtcNow;
    public int MinRssiDbm { get; set; }
    public int MaxRssiDbm { get; set; }
    public long SampleCount { get; set; }
}
