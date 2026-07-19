using CommunityToolkit.Mvvm.ComponentModel;
using StarSensing.Dashboard.Services;
using System.Windows.Media;

namespace StarSensing.Dashboard.Models;

public partial class SelectableNetwork : ObservableObject
{
    // ── Neon palette ──────────────────────────────────────────────────────
    private static readonly string[] Palette =
    {
        "#00f5d4", "#f5a623", "#4361ee", "#ef4444", "#00c853",
        "#7c3aed", "#06b6d4", "#ec4899", "#84cc16", "#f59e0b",
        "#10b981", "#e11d48", "#8b5cf6", "#fb923c", "#0ea5e9",
        "#22c55e", "#f97316", "#a855f7", "#14b8a6", "#6366f1"
    };

    private static int _globalColorIndex;

    // ── Static identity ───────────────────────────────────────────────────
    public string Bssid { get; }
    public string Ssid  { get; }

    /// <summary>Hex color string — used to create the ScottPlot chart line color.</summary>
    public string ChartColorHex { get; }

    /// <summary>WPF brush — bind to color dots and ProgressBar.Foreground in the UI.</summary>
    public SolidColorBrush ChartBrush { get; }

    /// <summary>RGB components for ScottPlot Color construction in code-behind.</summary>
    public (byte R, byte G, byte B) ChartRgb { get; }

    // ── Observable properties ─────────────────────────────────────────────
    [ObservableProperty]
    private bool _isSelected = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DistanceMeters))]
    [NotifyPropertyChangedFor(nameof(DistanceText))]
    [NotifyPropertyChangedFor(nameof(SignalLabel))]
    [NotifyPropertyChangedFor(nameof(SignalLabelBrush))]
    [NotifyPropertyChangedFor(nameof(RssiNormalized))]
    [NotifyPropertyChangedFor(nameof(RssiText))]
    private int _latestRssi;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BandChannelText))]
    private int _channel;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BandChannelText))]
    private string _band = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FrequencyText))]
    private int _frequencyKhz;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SignalQualityText))]
    private int _signalQuality;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FirstSeenText))]
    private DateTimeOffset? _firstSeen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LastSeenText))]
    private DateTimeOffset? _lastSeen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RssiRangeText))]
    private int _minRssiDbm;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RssiRangeText))]
    private int _maxRssiDbm;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SampleCountText))]
    private long _sampleCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VarianceText))]
    private double _variance;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MotionConfidenceText))]
    private double _motionConfidence;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ChangeRateText))]
    private double _changeRate;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EntropyText))]
    private double _entropy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CrossCorrelationText))]
    private double _crossCorrelation;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SmoothedRssiText))]
    private int _smoothedRssi;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AnomalyText))]
    private bool _isAnomaly;

    /// <summary>Compass bearing: 0=N, 90=E, clockwise.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DirectionText))]
    private double _bearingDegrees;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DirectionText))]
    [NotifyPropertyChangedFor(nameof(BearingSourceText))]
    private string _bearingSource = "estimated";

    public string BearingSourceText => BearingSource switch
    {
        "manual" => "Calibrated",
        "compass" => "Compass",
        "location" => "Saved location",
        _ => "Estimated"
    };

    public void SetBearing(double degrees, string source)
    {
        BearingDegrees = BearingStoreService.NormalizeDeg(degrees);
        BearingSource = source;
    }

    public void SetHistoricalActivity(double variance, double entropy)
    {
        Variance = variance;
        Entropy = entropy;
    }

    // ── Computed properties ───────────────────────────────────────────────

    /// <summary>
    /// Estimated distance in metres using the Log Distance Path Loss model:
    ///   d = 10 ^ ((TxPower − RSSI) / (10 × n))
    /// TxPower = −40 dBm at 1 m  |  n = 2.7 (typical indoor)
    /// </summary>
    public double DistanceMeters
    {
        get
        {
            const double txPower = -40.0;
            const double n       = 2.7;
            return Math.Pow(10.0, (txPower - LatestRssi) / (10.0 * n));
        }
    }

    public string DistanceText
    {
        get
        {
            double d = DistanceMeters;
            if (d < 1.0)   return "< 1 m";
            if (d > 200.0) return "> 200 m";
            return $"{d:F1} m";
        }
    }

    public string RssiText => $"{LatestRssi} dBm";

    public string FrequencyText => FrequencyKhz > 0 ? $"{FrequencyKhz / 1000.0:F1} MHz" : "Unknown";

    public string SignalQualityText => $"{SignalQuality}%";

    public string FirstSeenText => FirstSeen?.LocalDateTime.ToString("HH:mm:ss") ?? "-";

    public string LastSeenText => LastSeen?.LocalDateTime.ToString("HH:mm:ss") ?? "-";

    public string RssiRangeText => SampleCount > 0 ? $"{MinRssiDbm} to {MaxRssiDbm} dBm" : "-";

    public string SampleCountText => SampleCount.ToString("N0");

    public string VarianceText => $"{Variance:F2}";

    public string MotionConfidenceText => $"{MotionConfidence * 100.0:F0}%";

    public string ChangeRateText => $"{ChangeRate:F2} dBm/s";

    public string EntropyText => $"{Entropy:F2}";

    public string CrossCorrelationText => $"{CrossCorrelation:F2}";

    public string SmoothedRssiText => SmoothedRssi != 0 ? $"{SmoothedRssi} dBm" : "-";

    public string AnomalyText => IsAnomaly ? "YES" : "-";

    public static double EstimateBearingFromBssid(string bssid) => StableHash(bssid) % 360;

    public string DirectionText
    {
        get
        {
            double deg = BearingDegrees;
            string cardinal = deg switch
            {
                >= 337.5 or < 22.5 => "N",
                < 67.5 => "NE",
                < 112.5 => "E",
                < 157.5 => "SE",
                < 202.5 => "S",
                < 247.5 => "SW",
                < 292.5 => "W",
                _ => "NW"
            };
            return $"{cardinal} ({deg:F0}° · {BearingSourceText})";
        }
    }

    public string DeviceIdText => Bssid.Length >= 8 ? Bssid[..8] : Bssid;

    // Model lookup is not directly available from Wi-Fi scan APIs.
    public string ModelText =>
        Ssid.Contains("iPhone", StringComparison.OrdinalIgnoreCase) ? "Apple device (SSID hint)" :
        Ssid.Contains("Galaxy", StringComparison.OrdinalIgnoreCase) ? "Samsung device (SSID hint)" :
        Ssid.Contains("Pixel", StringComparison.OrdinalIgnoreCase) ? "Google device (SSID hint)" :
        Ssid.Contains("Router", StringComparison.OrdinalIgnoreCase) ? "Router/AP (SSID hint)" :
        "Unknown model";

    /// <summary>Formatted band + channel, e.g. "2.4 GHz · Ch 6".</summary>
    public string BandChannelText =>
        string.IsNullOrEmpty(Band)
            ? (Channel > 0 ? $"Ch {Channel}" : string.Empty)
            : (Channel > 0 ? $"{Band} · Ch {Channel}" : Band);

    /// <summary>0 = weakest (−100 dBm), 1 = strongest (−20 dBm).</summary>
    public double RssiNormalized => Math.Clamp((LatestRssi + 100.0) / 80.0, 0.0, 1.0);

    public string SignalLabel =>
        LatestRssi > -50 ? "Strong" :
        LatestRssi > -70 ? "Medium" : "Weak";

    public SolidColorBrush SignalLabelBrush =>
        LatestRssi > -50 ? _greenBrush  :
        LatestRssi > -70 ? _amberBrush  : _redBrush;

    // Pre-built static brushes to avoid allocations on every property get
    private static readonly SolidColorBrush _greenBrush = Frozen(0x00, 0xc8, 0x53);
    private static readonly SolidColorBrush _amberBrush = Frozen(0xf5, 0x9e, 0x0b);
    private static readonly SolidColorBrush _redBrush   = Frozen(0xef, 0x44, 0x44);

    private static SolidColorBrush Frozen(byte r, byte g, byte b)
    {
        var b2 = new SolidColorBrush(Color.FromRgb(r, g, b));
        b2.Freeze();
        return b2;
    }

    // ── Constructor ───────────────────────────────────────────────────────
    public SelectableNetwork(string bssid, string ssid)
    {
        Bssid = bssid;
        Ssid  = string.IsNullOrWhiteSpace(ssid) ? "Hidden" : ssid;

        int idx = Interlocked.Increment(ref _globalColorIndex) % Palette.Length;
        ChartColorHex = Palette[idx];

        var wc = (Color)ColorConverter.ConvertFromString(ChartColorHex);
        ChartBrush = new SolidColorBrush(wc);
        ChartBrush.Freeze();
        ChartRgb = (wc.R, wc.G, wc.B);
    }

    private static uint StableHash(string text)
    {
        const uint fnvOffset = 2166136261;
        const uint fnvPrime = 16777619;
        uint hash = fnvOffset;
        foreach (char c in text)
        {
            hash ^= char.ToUpperInvariant(c);
            hash *= fnvPrime;
        }
        return hash;
    }
}
