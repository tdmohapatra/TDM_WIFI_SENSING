namespace StarSensing.Core.Models;

/// <summary>
/// Real-time signal processing results for a specific AP.
/// </summary>
public sealed class ProcessedSignal
{
    /// <summary>BSSID of the access point.</summary>
    public string Bssid { get; set; } = string.Empty;

    /// <summary>SSID of the network.</summary>
    public string Ssid { get; set; } = string.Empty;

    /// <summary>Raw RSSI value in dBm.</summary>
    public int RawRssi { get; set; }

    /// <summary>Smoothed RSSI after moving average filter.</summary>
    public double SmoothedRssi { get; set; }

    /// <summary>RSSI variance over the analysis window.</summary>
    public double Variance { get; set; }

    /// <summary>Standard deviation of RSSI.</summary>
    public double StdDev { get; set; }

    /// <summary>Rate of RSSI change (dBm/sec).</summary>
    public double ChangeRate { get; set; }

    /// <summary>Dominant FFT frequency component (Hz).</summary>
    public double DominantFrequency { get; set; }

    /// <summary>FFT spectral energy in the motion-related band.</summary>
    public double SpectralEnergy { get; set; }

    /// <summary>Z-score anomaly value.</summary>
    public double ZScore { get; set; }

    /// <summary>Whether an anomaly was detected for this AP.</summary>
    public bool IsAnomaly { get; set; }

    /// <summary>Motion confidence for this specific AP (0.0 to 1.0).</summary>
    public double MotionConfidence { get; set; }

    /// <summary>Shannon entropy of RSSI in the analysis window.</summary>
    public double Entropy { get; set; }

    /// <summary>Mean Pearson correlation with other routers in the batch.</summary>
    public double CrossCorrelation { get; set; }

    /// <summary>Timestamp of the analysis.</summary>
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>FFT magnitude spectrum (first N bins).</summary>
    public double[] FftMagnitudes { get; set; } = Array.Empty<double>();

    /// <summary>FFT frequency bins (Hz).</summary>
    public double[] FftFrequencies { get; set; } = Array.Empty<double>();
}

/// <summary>
/// Aggregate analysis results across all APs.
/// </summary>
public sealed class EnvironmentState
{
    /// <summary>Timestamp of this state assessment.</summary>
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Overall motion confidence (0.0 to 1.0).</summary>
    public double MotionConfidence { get; set; }

    /// <summary>Occupancy estimation (0.0 to 1.0).</summary>
    public double OccupancyConfidence { get; set; }

    /// <summary>Current environmental classification.</summary>
    public EnvironmentClassification Classification { get; set; }

    /// <summary>Per-AP processed signals.</summary>
    public List<ProcessedSignal> Signals { get; set; } = new();

    /// <summary>Active motion events.</summary>
    public List<MotionEvent> ActiveEvents { get; set; } = new();

    /// <summary>Spatial zones with current state.</summary>
    public List<SpatialZone> Zones { get; set; } = new();

    /// <summary>Number of active APs being monitored.</summary>
    public int ActiveApCount { get; set; }

    /// <summary>Overall signal stability index (0.0 = chaotic, 1.0 = perfectly stable).</summary>
    public double StabilityIndex { get; set; }

    /// <summary>LSTM sequence-model motion estimate (0.0 to 1.0).</summary>
    public double LstmMotionConfidence { get; set; }

    /// <summary>CNN spatial heatmap activity score (0.0 to 1.0).</summary>
    public double CnnActivityScore { get; set; }
}

/// <summary>
/// High-level environment classification.
/// </summary>
public enum EnvironmentClassification
{
    /// <summary>Environment is stable, no activity.</summary>
    Static = 0,

    /// <summary>Low-level fluctuations, possible distant movement.</summary>
    LowActivity = 1,

    /// <summary>Moderate fluctuations, likely nearby movement.</summary>
    ModerateActivity = 2,

    /// <summary>High fluctuations, significant movement.</summary>
    HighActivity = 3,

    /// <summary>Environmental transition (door open/close, etc).</summary>
    Transition = 4
}
