namespace StarSensing.Core.Models;

/// <summary>Persisted per-router feature vector for one scan batch.</summary>
public sealed class WiFiFeatureRecord
{
    public string BatchId { get; set; } = string.Empty;
    public string Bssid { get; set; } = string.Empty;
    public string Ssid { get; set; } = string.Empty;
    public long TimestampMs { get; set; }
    public int RawRssi { get; set; }
    public double SmoothedRssi { get; set; }
    public double Variance { get; set; }
    public double StdDev { get; set; }
    public double ChangeRate { get; set; }
    public double Entropy { get; set; }
    public double CrossCorrelation { get; set; }
    public double DominantFrequency { get; set; }
    public double SpectralEnergy { get; set; }
    public double ZScore { get; set; }
    public bool IsAnomaly { get; set; }
    public double MotionConfidence { get; set; }
}
