using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Grpc.Core;
using StarSensing.Core.Protos;
using StarSensing.Dashboard.Models;
using StarSensing.Dashboard.Services;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;

namespace StarSensing.Dashboard.ViewModels;

public enum SignalSourceMode
{
    Selected = 0,
    All = 1,
    Connected = 2
}

public partial class AreaMapViewModel : ObservableObject
{
    private readonly GrpcClientService _grpc;
    private readonly NetworkFilterManager _filterManager;
    private CancellationTokenSource? _measurementStreamCts;
    private readonly EnvironmentStreamService? _envStream;
    private readonly LocationStoreService _locations = new();
    private readonly BearingStoreService _bearingStore;
    private readonly CompassService _compass;
    private readonly HistoricalTimelineService? _timeline;
    private readonly TimeRangeService? _timeRange;
    private bool _savedNetworksLoaded;

    public TimeRangeService? TimeRange => _timeRange;
    public HistoricalTimelineService? Timeline => _timeline;

    /// <summary>Calibrated bearing (deg) per BSSID — synced from BearingStoreService.</summary>
    public Dictionary<string, double> SavedBearings { get; } = new(StringComparer.OrdinalIgnoreCase);

    public event Action? PositionsUpdated;

    [ObservableProperty]
    private SelectableNetwork? _calibrationTarget;

    [ObservableProperty]
    private double _calibrationBearing;

    [ObservableProperty]
    private double _northOffsetDeg;

    [ObservableProperty]
    private string _compassStatus = "Compass not checked";

    [ObservableProperty]
    private bool _calibrationMode;

    [ObservableProperty]
    private double _motionConfidence;

    [ObservableProperty]
    private string _classification = "STATIC";

    [ObservableProperty]
    private int _activeApCount;

    [ObservableProperty]
    private int _timeFrameSeconds = 0;

    [ObservableProperty]
    private int _sampleIntervalMs = 50;

    [ObservableProperty]
    private double _measurementLatencyMs;

    [ObservableProperty]
    private double _environmentLatencyMs;

    [ObservableProperty]
    private double _lstmMotionPct;

    [ObservableProperty]
    private double _cnnActivityPct;

    [ObservableProperty]
    private string _positionSource = "Polar estimate";

    [ObservableProperty]
    private SignalSourceMode _selectedSignalSource = SignalSourceMode.Selected;

    [ObservableProperty]
    private string _connectedBssid = string.Empty;

    [ObservableProperty]
    private string _connectedSsid = string.Empty;

    public string TimeFrameText => TimeFrameSeconds > 0
        ? $"{TimeFrameSeconds}s window @ {SampleIntervalMs}ms"
        : $"Live @ {SampleIntervalMs}ms";

    public ObservableCollection<ZoneDisplayItem> ActiveZones { get; } = new();

    [ObservableProperty]
    private int _activeZoneCount;

    partial void OnTimeFrameSecondsChanged(int value) => OnPropertyChanged(nameof(TimeFrameText));

    partial void OnSampleIntervalMsChanged(int value)
    {
        OnPropertyChanged(nameof(TimeFrameText));
        RestartMeasurementStream();
        _envStream?.SetIntervalMs(value);
    }

    public event Action<MeasurementBatch>? OnMeasurementsReceived;
    public event Action<EnvironmentStateMsg>? OnStateReceived;

    public AreaMapViewModel(GrpcClientService grpc)
        : this(grpc, new NetworkFilterManager())
    {
    }

    public AreaMapViewModel(GrpcClientService grpc, NetworkFilterManager filterManager)
        : this(grpc, filterManager, null) { }

    public AreaMapViewModel(GrpcClientService grpc, NetworkFilterManager filterManager, EnvironmentStreamService? envStream)
        : this(grpc, filterManager, envStream, new BearingStoreService(), new CompassService()) { }

    public AreaMapViewModel(GrpcClientService grpc, NetworkFilterManager filterManager, EnvironmentStreamService? envStream, BearingStoreService bearingStore, CompassService compass)
        : this(grpc, filterManager, envStream, bearingStore, compass, null, null) { }

    public AreaMapViewModel(
        GrpcClientService grpc,
        NetworkFilterManager filterManager,
        EnvironmentStreamService? envStream,
        BearingStoreService bearingStore,
        CompassService compass,
        HistoricalTimelineService? timeline,
        TimeRangeService? timeRange)
    {
        _grpc = grpc;
        _filterManager = filterManager;
        _envStream = envStream;
        _bearingStore = bearingStore;
        _compass = compass;
        _timeline = timeline;
        _timeRange = timeRange;
        Networks = _filterManager.Networks;
        SelectedNetworks = _filterManager.SelectedNetworks;
        _bearingStore.BearingsChanged += () => _ = RefreshSavedPositionsAsync();
        CompassStatus = _compass.StatusText;
        _ = InitializeBearingsAsync();
        _ = LoadSavedNetworksWhenReadyAsync();
        RestartMeasurementStream();
        if (_envStream != null)
            _envStream.StateReceived += OnSharedEnvironmentState;
        if (_timeline != null)
            _timeline.FrameChanged += OnHistoricalFrame;
        _ = RefreshConnectedNetworkLoopAsync();
    }

    private async Task LoadSavedNetworksWhenReadyAsync()
    {
        for (int i = 0; i < 30 && !_savedNetworksLoaded; i++)
        {
            if (_grpc.Client != null)
            {
                await LoadSavedNetworksAsync();
                _savedNetworksLoaded = true;
                return;
            }

            await Task.Delay(500);
        }
    }

    private void OnHistoricalFrame(ReplayFrame frame)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            MotionConfidence = frame.MotionConfidence * 100.0;
            Classification = frame.Classification;
            ActiveApCount = frame.ActiveApCount;
            LstmMotionPct = frame.LstmMotionConfidence * 100.0;
            CnnActivityPct = frame.CnnActivityScore * 100.0;
            UpdateActiveZonesFromStored(frame.Zones);
            foreach (var ap in frame.ApFeatures)
            {
                var net = _filterManager.GetNetwork(ap.Bssid);
                net?.SetHistoricalActivity(ap.Variance, ap.Entropy);
            }
            PositionsUpdated?.Invoke();
        });
    }

    private async Task InitializeBearingsAsync()
    {
        await _bearingStore.InitializeAsync();
        NorthOffsetDeg = _bearingStore.NorthOffsetDeg;
        await RefreshSavedPositionsAsync();
    }

    partial void OnCalibrationTargetChanged(SelectableNetwork? value)
    {
        if (value != null)
            CalibrationBearing = value.BearingDegrees;
    }

    partial void OnNorthOffsetDegChanged(double value)
    {
        _ = _bearingStore.SetNorthOffsetAsync(value);
    }

    [RelayCommand]
    private async Task SaveCalibrationBearingAsync()
    {
        if (CalibrationTarget == null) return;
        await _bearingStore.SetBearingAsync(CalibrationTarget.Bssid, CalibrationBearing, "manual");
        CalibrationTarget.SetBearing(CalibrationBearing, "manual");
        await RefreshSavedPositionsAsync();
    }

    [RelayCommand]
    private async Task CaptureCompassBearingAsync()
    {
        if (CalibrationTarget == null)
        {
            CompassStatus = "Select a router on the map first.";
            return;
        }

        var heading = _compass.TryGetHeading();
        if (heading == null)
        {
            CompassStatus = _compass.StatusText;
            return;
        }

        double bearing = _bearingStore.CompassToMapBearing(heading.Value);
        CalibrationBearing = bearing;
        await _bearingStore.SetBearingAsync(CalibrationTarget.Bssid, bearing, "compass");
        CalibrationTarget.SetBearing(bearing, "compass");
        CompassStatus = $"Captured {bearing:F0}° (device {heading.Value:F0}°)";
        await RefreshSavedPositionsAsync();
    }

    public void SetCalibrationTargetByBssid(string? bssid)
    {
        if (string.IsNullOrEmpty(bssid))
        {
            CalibrationTarget = null;
            return;
        }

        CalibrationTarget = _filterManager.GetNetwork(bssid);
        if (CalibrationTarget != null)
            CalibrationBearing = CalibrationTarget.BearingDegrees;
    }

    public async Task SetBearingFromMapAsync(string bssid, double bearingDeg)
    {
        await _bearingStore.SetBearingAsync(bssid, bearingDeg, "manual");
        var net = _filterManager.GetNetwork(bssid);
        net?.SetBearing(bearingDeg, "manual");
        if (CalibrationTarget?.Bssid == bssid)
            CalibrationBearing = bearingDeg;
        await RefreshSavedPositionsAsync();
    }

    private void OnSharedEnvironmentState(EnvironmentStateMsg state)
    {
        if (_timeRange?.IsLiveMode == false) return;

        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            if (state.Timestamp != null)
                EnvironmentLatencyMs = Math.Max(0, (DateTimeOffset.UtcNow - state.Timestamp.ToDateTimeOffset()).TotalMilliseconds);

            MotionConfidence = state.MotionConfidence * 100.0;
            Classification = state.Classification.ToString().Replace("_", " ");
            ActiveApCount = state.ActiveApCount;
            LstmMotionPct = state.LstmMotionConfidence * 100.0;
            CnnActivityPct = state.CnnActivityScore * 100.0;

            UpdateActiveZones(state.Zones);

            foreach (var sig in state.Signals)
                _filterManager.UpdateProcessedSignal(sig);

            OnStateReceived?.Invoke(state);
        }, DispatcherPriority.Background);
    }

    public ObservableCollection<SelectableNetwork> Networks { get; }

    public ObservableCollection<SelectableNetwork> SelectedNetworks { get; }

    public bool IsSelected(string bssid) => _filterManager.IsSelected(bssid);

    public SelectableNetwork? GetNetwork(string bssid) => _filterManager.GetNetwork(bssid);

    public bool IsConnectedNetwork(SelectableNetwork network)
    {
        if (!string.IsNullOrWhiteSpace(ConnectedBssid))
            return string.Equals(network.Bssid, ConnectedBssid, StringComparison.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(ConnectedSsid))
            return string.Equals(network.Ssid, ConnectedSsid, StringComparison.OrdinalIgnoreCase);

        return false;
    }

    private async Task RefreshConnectedNetworkLoopAsync()
    {
        while (true)
        {
            try
            {
                await RefreshConnectedNetworkAsync();
            }
            catch { }

            await Task.Delay(TimeSpan.FromSeconds(3));
        }
    }

    private async Task RefreshConnectedNetworkAsync()
    {
        var psi = new ProcessStartInfo("netsh", "wlan show interfaces")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = psi };
        process.Start();
        string output = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();

        string bssid = string.Empty;
        string ssid = string.Empty;
        bool connected = false;

        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("State", StringComparison.OrdinalIgnoreCase) &&
                line.Contains("connected", StringComparison.OrdinalIgnoreCase))
                connected = true;

            if (line.StartsWith("SSID", StringComparison.OrdinalIgnoreCase) &&
                !line.StartsWith("SSID name", StringComparison.OrdinalIgnoreCase) &&
                !line.StartsWith("BSSID", StringComparison.OrdinalIgnoreCase))
            {
                int idx = line.IndexOf(':');
                if (idx >= 0) ssid = line[(idx + 1)..].Trim();
            }

            if (line.StartsWith("BSSID", StringComparison.OrdinalIgnoreCase))
            {
                int idx = line.IndexOf(':');
                if (idx >= 0) bssid = line[(idx + 1)..].Trim();
            }
        }

        Application.Current.Dispatcher.Invoke(() =>
        {
            if (connected)
            {
                ConnectedBssid = bssid;
                ConnectedSsid = ssid;
            }
            else
            {
                ConnectedBssid = string.Empty;
                ConnectedSsid = string.Empty;
            }
        });
    }

    private async Task LoadSavedNetworksAsync()
    {
        if (_grpc.Client == null) return;

        try
        {
            var saved = await _grpc.Client.GetSavedNetworksAsync(new SavedNetworkRequest { MaxAgeSeconds = 0 });
            Application.Current?.Dispatcher.Invoke(() =>
            {
                _filterManager.BeginBatchImport();
                try
                {
                    foreach (var network in saved.Networks)
                        _filterManager.UpdateSavedNetwork(network);
                }
                finally
                {
                    _filterManager.EndBatchImport();
                }
            });
        }
        catch { }
    }

    public async Task RefreshSavedPositionsAsync()
    {
        var bearings = _bearingStore.GetAllBearings();
        Application.Current.Dispatcher.Invoke(() =>
        {
            SavedBearings.Clear();
            foreach (var kv in bearings)
                SavedBearings[kv.Key] = kv.Value;

            int calibrated = bearings.Count;
            int estimated = Networks.Count(n => n.BearingSource == "estimated");
            PositionSource = calibrated > 0
                ? $"Live distance · {calibrated} calibrated, {estimated} estimated"
                : "Live distance · estimated bearing (click AP on map to calibrate)";
            PositionsUpdated?.Invoke();
        });
    }

    private void RestartMeasurementStream()
    {
        _measurementStreamCts?.Cancel();
        _measurementStreamCts = new CancellationTokenSource();
        _ = SubscribeToMeasurements(_measurementStreamCts.Token);
    }

    private async Task SubscribeToMeasurements(CancellationToken ct)
    {
        if (_grpc.Client == null) return;

        try
        {
            var call = _grpc.Client.StreamMeasurements(
                new StreamRequest { MinIntervalMs = SampleIntervalMs },
                cancellationToken: ct);

            await foreach (var batch in call.ResponseStream.ReadAllAsync(ct))
            {
                var dispatcher = Application.Current?.Dispatcher;
                if (dispatcher == null)
                    continue;

                if (dispatcher.CheckAccess())
                    ApplyMeasurementBatch(batch);
                else
                    dispatcher.BeginInvoke(() => ApplyMeasurementBatch(batch), DispatcherPriority.Background);
            }
        }
        catch (OperationCanceledException) { }
        catch { }
    }

    private void ApplyMeasurementBatch(MeasurementBatch batch)
    {
        if (batch.Timestamp != null)
            MeasurementLatencyMs = Math.Max(0, (DateTimeOffset.UtcNow - batch.Timestamp.ToDateTimeOffset()).TotalMilliseconds);

        foreach (var m in batch.Measurements)
        {
            string band = m.FrequencyKhz is > 5000000 ? "5 GHz" :
                          m.FrequencyKhz is > 0 ? "2.4 GHz" : "";
            _filterManager.UpdateNetwork(m.Bssid, m.Ssid, m.RssiDbm, m.Channel, band, m.SignalQuality, m.FrequencyKhz);
        }

        OnMeasurementsReceived?.Invoke(batch);
    }

    private void UpdateActiveZones(IEnumerable<SpatialZoneMsg> zones)
    {
        ActiveZones.Clear();
        foreach (var z in zones.OrderByDescending(z => z.OccupancyConfidence))
        {
            ActiveZones.Add(new ZoneDisplayItem
            {
                ZoneId = z.ZoneId,
                Name = z.Name,
                X = z.X,
                Y = z.Y,
                Radius = z.Radius,
                OccupancyPct = z.OccupancyConfidence * 100.0,
                MotionPct = z.MotionConfidence * 100.0,
                ColorHex = string.IsNullOrWhiteSpace(z.Color) ? "#4361ee" : z.Color,
                Summary = $"Occupancy {z.OccupancyConfidence * 100:F0}% · Motion {z.MotionConfidence * 100:F0}%"
            });
        }

        ActiveZoneCount = ActiveZones.Count;
    }

    private void UpdateActiveZonesFromStored(IEnumerable<StoredZone> zones)
    {
        ActiveZones.Clear();
        foreach (var z in zones.OrderByDescending(z => z.OccupancyConfidence))
        {
            ActiveZones.Add(new ZoneDisplayItem
            {
                ZoneId = z.ZoneId,
                Name = z.Name,
                X = z.X,
                Y = z.Y,
                Radius = z.Radius,
                OccupancyPct = z.OccupancyConfidence * 100.0,
                MotionPct = z.MotionConfidence * 100.0,
                ColorHex = string.IsNullOrWhiteSpace(z.Color) ? "#4361ee" : z.Color,
                Summary = $"Occupancy {z.OccupancyConfidence * 100:F0}% · Motion {z.MotionConfidence * 100:F0}%"
            });
        }

        ActiveZoneCount = ActiveZones.Count;
    }
}
