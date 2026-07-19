using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Grpc.Core;
using StarSensing.Core.Protos;
using StarSensing.Dashboard.Models;
using StarSensing.Dashboard.Services;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Data;

namespace StarSensing.Dashboard.ViewModels;

public partial class HeatmapViewModel : ObservableObject
{
    private readonly GrpcClientService _grpc;
    private readonly NetworkFilterManager _filter;
    private readonly LocationStoreService _locations = new();
    private readonly BearingStoreService? _bearingStore;
    private readonly EnvironmentStreamService _envStream;
    private readonly TimeRangeService _timeRange;
    private readonly HistoricalTimelineService _timeline;
    private readonly SensingDataService _data;
    private readonly SpatialMemoryStore _spatialMemory = new();

    public event Action<MeasurementBatch>? OnDataReceived;
    public event Action<double[,]>? OnSpatialFrameReady;
    public event Action? OnChannelHistoryReady;
    public event Action? BlockZonesUpdated;
    public event Action? SpatialMemoryUpdated;
    public event Action? ProbeFired;

    private readonly List<(string Bssid, double X, double Y, double Activity)> _activitySplats = new();
    public List<MotionTrailSample> MotionTrails { get; } = new();
    private readonly Dictionary<string, (double X, double Y)> _lastZonePos = new(StringComparer.OrdinalIgnoreCase);
    private long _trailSequence;
    private const int MaxTrailSamples = 720;
    public const float TrailMaxAgeSeconds = 28f;

    public SpatialMemoryStore SpatialMemory => _spatialMemory;

    [ObservableProperty]
    private string _modeLabel = "Channel";

    [ObservableProperty]
    private bool _spatialMode;

    [ObservableProperty]
    private double _peakActivity;

    [ObservableProperty]
    private string _positionSource = "Polar estimate";

    [ObservableProperty]
    private double _cnnActivityScore;

    [ObservableProperty]
    private string _timeModeLabel = "Live";

    [ObservableProperty]
    private string _timelineStatus = "";

    [ObservableProperty]
    private string? _selectedApBssid;

    [ObservableProperty]
    private string _apSearchText = "";

    [ObservableProperty]
    private double _motionConfidence;

    [ObservableProperty]
    private double _occupancyConfidence;

    [ObservableProperty]
    private double _viewportMeters = 6.0;

    [ObservableProperty]
    private string? _selectedZoneId;

    [ObservableProperty]
    private string? _probingZoneId;

    [ObservableProperty]
    private double _probeDistanceMeters;

    [ObservableProperty]
    private string _probeStatus = "Ready — select zone and Ping";

    [ObservableProperty]
    private int _spatialStreamMs = 50;

    public ObservableCollection<SpatialApRow> ApPoints { get; } = new();
    public ObservableCollection<SpatialBlockZone> BlockZones { get; } = new();
    public ObservableCollection<SpatialHotspot> CommonHotspots { get; } = new();

    public ICollectionView ApPointsView { get; }

    public IEnumerable<SpatialApRow> FilteredApPoints => ApPoints.OrderByDescending(a => a.Activity);

    partial void OnApSearchTextChanged(string value)
    {
        ApPointsView.Refresh();
        OnPropertyChanged(nameof(FilteredApPoints));
    }

    public const int FloorSize = 128;
    public const int TimeSteps = 60;
    public const double MinViewportMeters = 0.1;
    public const double MaxViewportMeters = 50.0;
    public const double DefaultViewportMeters = 6.0;

    private Dictionary<string, double> _routerBearings = new(StringComparer.OrdinalIgnoreCase);

    public TimeRangeService TimeRange => _timeRange;
    public HistoricalTimelineService Timeline => _timeline;

    public HeatmapViewModel(
        GrpcClientService grpc,
        NetworkFilterManager filter,
        EnvironmentStreamService envStream,
        BearingStoreService? bearingStore,
        TimeRangeService timeRange,
        HistoricalTimelineService timeline,
        SensingDataService data)
    {
        _grpc = grpc;
        _filter = filter;
        _bearingStore = bearingStore;
        _envStream = envStream;
        _timeRange = timeRange;
        _timeline = timeline;
        _data = data;
        ApPointsView = CollectionViewSource.GetDefaultView(ApPoints);
        ApPointsView.SortDescriptions.Add(new SortDescription(nameof(SpatialApRow.Activity), ListSortDirection.Descending));
        ApPointsView.Filter = FilterApRow;
        _ = SubscribeToMeasurements();
        _ = LoadRouterPositionsAsync();
        envStream.StateReceived += OnEnvironmentState;
        _timeline.FrameChanged += OnHistoricalFrame;
        _timeline.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(HistoricalTimelineService.StatusText))
                TimelineStatus = _timeline.StatusText;
        };
        _timeRange.PropertyChanged += async (_, e) =>
        {
            if (e.PropertyName is nameof(TimeRangeService.IsLiveMode) or nameof(TimeRangeService.RangeMinutes))
            {
                TimeModeLabel = _timeRange.ModeLabel;
                if (!_timeRange.IsLiveMode)
                    await _timeline.ReloadAsync();
                else if (!SpatialMode)
                    await LoadHistoryAsync();
            }
        };
        if (bearingStore != null)
            bearingStore.BearingsChanged += () => _ = LoadRouterPositionsAsync();
        _ = _spatialMemory.LoadAsync();
    }

    partial void OnSpatialModeChanged(bool value)
    {
        ModeLabel = value ? "3D Spatial Heatmap" : "Channel";
        if (value)
        {
            _envStream.SetIntervalMs(SpatialStreamMs);
            TimeModeLabel = $"Live @ {SpatialStreamMs}ms (spatial)";
        }
    }

    partial void OnViewportMetersChanged(double value)
    {
        RecalcZoneDistances();
        RefreshCommonHotspots();
    }

    public string ViewportRangeLabel => ViewportMeters < 1.0
        ? $"Range {ViewportMeters * 100:F0} cm"
        : $"Range {ViewportMeters:F1} m";

    private void RecalcZoneDistances()
    {
        for (int i = 0; i < BlockZones.Count; i++)
        {
            var z = BlockZones[i];
            var (dist, bearing) = ZonePolar(z.NormalizedX, z.NormalizedY);
            BlockZones[i] = new SpatialBlockZone
            {
                ZoneId = z.ZoneId,
                Name = z.Name,
                NormalizedX = z.NormalizedX,
                NormalizedY = z.NormalizedY,
                RadiusNorm = z.RadiusNorm,
                MotionPct = z.MotionPct,
                OccupancyPct = z.OccupancyPct,
                DistanceMeters = dist,
                BearingDeg = bearing
            };
        }

        OnPropertyChanged(nameof(ViewportRangeLabel));
        BlockZonesUpdated?.Invoke();
    }

    public void TickMotionTrails(float dt)
    {
        for (int i = MotionTrails.Count - 1; i >= 0; i--)
        {
            MotionTrails[i].Age += dt;
            if (MotionTrails[i].Age > TrailMaxAgeSeconds)
                MotionTrails.RemoveAt(i);
        }
    }

    [RelayCommand]
    private async Task ClearSpatialMemoryAsync()
    {
        await _spatialMemory.ClearAsync();
        CommonHotspots.Clear();
        MotionTrails.Clear();
        _lastZonePos.Clear();
        ProbeStatus = "Spatial memory cleared";
        SpatialMemoryUpdated?.Invoke();
    }

    [RelayCommand]
    private void PingSelectedZone()
    {
        var zone = BlockZones.FirstOrDefault(z =>
            string.Equals(z.ZoneId, SelectedZoneId, StringComparison.OrdinalIgnoreCase));
        if (zone == null)
        {
            ProbeStatus = "Select a detection block first";
            return;
        }

        ProbingZoneId = zone.ZoneId;
        ProbeDistanceMeters = zone.DistanceMeters;
        ProbeStatus = zone.DistanceMeters < 1.0
            ? $"Pinging → {zone.Name} @ {zone.DistanceMeters * 100:F0} cm · {zone.BearingDeg:F0}°"
            : $"Pinging → {zone.Name} @ {zone.DistanceMeters:F2} m · {zone.BearingDeg:F0}°";
        ProbeFired?.Invoke();
    }

    [RelayCommand]
    private void SetSpatialFastStream()
    {
        SpatialStreamMs = 50;
        _envStream.SetIntervalMs(50);
        if (SpatialMode)
            TimeModeLabel = "Live @ 50ms (spatial)";
    }

    private void OnHistoricalFrame(ReplayFrame frame)
    {
        if (_timeRange.IsLiveMode) return;

        Application.Current.Dispatcher.Invoke(() =>
        {
            PeakActivity = frame.AvgVariance;
            CnnActivityScore = frame.CnnActivityScore * 100.0;
            MotionConfidence = frame.MotionConfidence * 100.0;
            OccupancyConfidence = frame.OccupancyConfidence * 100.0;
            UpdateBlockZonesFromStored(frame.Zones);
            BuildActivitySplatsFromHistory(frame);
            if (SpatialMode)
                OnSpatialFrameReady?.Invoke(BuildSpatialGridFromStoredZones(frame.Zones, frame));
        });
    }

    private void OnEnvironmentState(EnvironmentStateMsg state)
    {
        if (!_timeRange.IsLiveMode) return;

        if (SpatialMode)
        {
            MotionConfidence = state.MotionConfidence * 100.0;
            OccupancyConfidence = state.OccupancyConfidence * 100.0;
            UpdateBlockZonesFromLive(state.Zones);
            BuildActivitySplatsFromLive(state);
            double peak = BlockZones.Count > 0
                ? BlockZones.Max(z => z.MotionPct)
                : _activitySplats.Count > 0 ? _activitySplats.Max(s => s.Activity) * 100 : 0;
            PeakActivity = peak / 100.0;
            CnnActivityScore = state.CnnActivityScore * 100.0;
            AccumulateSpatialMemory(state);
            OnSpatialFrameReady?.Invoke(BuildSpatialGridFromLive(state));
        }
    }

    private void AccumulateSpatialMemory(EnvironmentStateMsg state)
    {
        _spatialMemory.Decay();

        foreach (var z in state.Zones)
        {
            double motion = Math.Max(z.MotionConfidence, z.OccupancyConfidence * 0.35);
            if (motion > 0.03)
                _spatialMemory.AccumulateMotion(z.X, z.Y, motion, 4);
        }

        AccumulateObstaclesFromSignals(state.Signals);

        foreach (var s in state.Signals)
        {
            double motion = Math.Max(s.MotionConfidence, s.Variance / 6.0);
            if (motion < 0.06) continue;
            var (x, y) = GetPosition(s.Bssid);
            _spatialMemory.AccumulateMotion(x, y, motion * 0.65, 2);
            AddPrecisionTrail($"sig-{s.Bssid}", x, y, (float)motion, false);
        }

        RefreshCommonHotspots();
        _spatialMemory.RequestSave();
        SpatialMemoryUpdated?.Invoke();
    }

    private void AccumulateObstaclesFromSignals(IEnumerable<ProcessedSignalMsg> signals)
    {
        const double txPower = -40.0;
        const double n = 2.7;

        foreach (var s in signals)
        {
            var net = _filter.GetNetwork(s.Bssid);
            double distance = net?.DistanceMeters ?? EstimateDistanceFromRssi(s.RawRssi);
            double expectedRssi = txPower - 10 * n * Math.Log10(Math.Max(0.05, distance));
            double excessLoss = expectedRssi - s.RawRssi;
            if (excessLoss < 7) continue;

            var (apX, apY) = GetPosition(s.Bssid);
            double t = Math.Clamp(0.12 + excessLoss / 55.0, 0.1, 0.85);
            double ox = 0.5 + (apX - 0.5) * t;
            double oy = 0.5 + (apY - 0.5) * t;
            double strength = Math.Clamp(excessLoss / 35.0, 0.08, 1.0);
            _spatialMemory.AccumulateObstacle(ox, oy, strength, 3);
            AddPrecisionTrail($"obs-{s.Bssid}", ox, oy, (float)strength, true);
        }
    }

    private static double EstimateDistanceFromRssi(int rssi)
    {
        const double txPower = -40.0;
        const double pathN = 2.7;
        return Math.Pow(10, (txPower - rssi) / (10 * pathN));
    }

    private void RefreshCommonHotspots()
    {
        CommonHotspots.Clear();
        // Fixed calibration span (matches GetPosition's MetersPolarToNormalized scale) — keeps
        // reported hotspot distance stable and accurate regardless of the live zoom level.
        foreach (var h in _spatialMemory.ExtractHotspots(MaxViewportMeters))
            CommonHotspots.Add(h);
    }

    private double[,] BuildSpatialGridFromLive(EnvironmentStateMsg state)
    {
        var grid = new double[FloorSize, FloorSize];
        foreach (var z in state.Zones)
            PaintZoneBlock(grid, z.X, z.Y, z.Radius, Math.Max(z.MotionConfidence, z.OccupancyConfidence * 0.45));

        BlendMemoryIntoGrid(grid);

        foreach (var s in state.Signals)
        {
            double motion = Math.Max(s.MotionConfidence, s.Variance / 6.0);
            if (motion < 0.04) continue;
            PaintMotionBlob(grid, s.Bssid, motion);
        }

        return grid;
    }

    private double[,] BuildSpatialGridFromStoredZones(IEnumerable<StoredZone> zones, ReplayFrame frame)
    {
        var grid = new double[FloorSize, FloorSize];
        foreach (var z in zones)
            PaintZoneBlock(grid, z.X, z.Y, z.Radius, Math.Max(z.MotionConfidence, z.OccupancyConfidence * 0.45));

        foreach (var ap in frame.ApFeatures)
        {
            double motion = Math.Max(ap.MotionConfidence, ap.Variance / 6.0);
            if (motion < 0.04) continue;
            PaintMotionBlob(grid, ap.Bssid, motion);
        }

        PeakActivity = frame.ApFeatures.Count > 0
            ? frame.ApFeatures.Max(a => Math.Max(a.Variance, a.Entropy * 2.0))
            : frame.AvgVariance;
        return grid;
    }

    private void PaintZoneBlock(double[,] grid, double nx, double ny, double radiusNorm, double strength)
    {
        int cx = (int)(nx * (FloorSize - 1));
        int cy = (int)(ny * (FloorSize - 1));
        int blockRadius = Math.Max(2, (int)(radiusNorm * FloorSize * 0.42));
        for (int dr = -blockRadius; dr <= blockRadius; dr++)
        for (int dc = -blockRadius; dc <= blockRadius; dc++)
        {
            int nr = cy + dr, nc = cx + dc;
            if (nr < 0 || nr >= FloorSize || nc < 0 || nc >= FloorSize) continue;
            double dist = Math.Sqrt(dr * dr + dc * dc);
            if (dist > blockRadius) continue;
            double edge = 1.0 - dist / Math.Max(1, blockRadius);
            grid[nr, nc] = Math.Max(grid[nr, nc], strength * (0.55 + edge * 0.45));
        }
    }

    private void BlendMemoryIntoGrid(double[,] grid)
    {
        for (int r = 0; r < FloorSize; r++)
        for (int c = 0; c < FloorSize; c++)
        {
            double motionMem = _spatialMemory.MotionMemory[r, c];
            double obsMem = _spatialMemory.ObstacleMemory[r, c];
            grid[r, c] = Math.Max(grid[r, c], motionMem * 0.85);
            if (obsMem > 0.05)
                grid[r, c] = Math.Max(grid[r, c], obsMem * 0.55);
        }
    }

    private void PaintMotionBlob(double[,] grid, string bssid, double motion)
    {
        var (x, y) = GetPosition(bssid);
        int col = (int)(x * (FloorSize - 1));
        int row = (int)(y * (FloorSize - 1));
        int radius = 2;
        for (int dr = -radius; dr <= radius; dr++)
        for (int dc = -radius; dc <= radius; dc++)
        {
            int nr = row + dr, nc = col + dc;
            if (nr < 0 || nr >= FloorSize || nc < 0 || nc >= FloorSize) continue;
            double dist = Math.Sqrt(dr * dr + dc * dc);
            double falloff = Math.Exp(-dist * dist / 1.8);
            grid[nr, nc] = Math.Max(grid[nr, nc], motion * falloff * 0.65);
        }
    }

    private async Task LoadRouterPositionsAsync()
    {
        Dictionary<string, double> bearings;
        if (_bearingStore != null)
        {
            await _bearingStore.InitializeAsync();
            bearings = new Dictionary<string, double>(_bearingStore.GetAllBearings(), StringComparer.OrdinalIgnoreCase);
        }
        else
            bearings = new Dictionary<string, double>(await _locations.GetRouterBearingsAsync(), StringComparer.OrdinalIgnoreCase);

        Application.Current.Dispatcher.Invoke(() =>
        {
            _routerBearings = bearings;
            PositionSource = _routerBearings.Count > 0
                ? $"Live distance · calibrated bearing ({_routerBearings.Count} routers)"
                : "Live distance · estimated bearing (calibrate on Area Map)";
        });
    }

    private (double X, double Y) GetPosition(string bssid)
    {
        var net = _filter.GetNetwork(bssid);
        double bearing = _routerBearings.TryGetValue(bssid, out var saved)
            ? saved
            : net?.BearingDegrees ?? SelectableNetwork.EstimateBearingFromBssid(bssid);
        double distance = net?.DistanceMeters ?? 12.0;
        return LocationStoreService.MetersPolarToNormalized(bearing, distance, MaxViewportMeters);
    }

    public IReadOnlyList<(string Bssid, double X, double Y, double Activity)> ActivitySplats => _activitySplats;

    public IEnumerable<(string Bssid, (double X, double Y) Pos)> RouterPositions =>
        _filter.Networks.Select(n => (n.Bssid, GetPosition(n.Bssid)));

    [RelayCommand]
    private void SelectAp(string? bssid) => SelectedApBssid = bssid;

    private bool FilterApRow(object item)
    {
        if (item is not SpatialApRow row) return false;
        if (string.IsNullOrWhiteSpace(ApSearchText)) return true;
        var q = ApSearchText.Trim();
        return row.Ssid.Contains(q, StringComparison.OrdinalIgnoreCase)
            || row.Bssid.Contains(q, StringComparison.OrdinalIgnoreCase);
    }

    private void UpdateBlockZonesFromLive(IEnumerable<SpatialZoneMsg> zones)
    {
        var rows = new List<SpatialBlockZone>();
        foreach (var z in zones.OrderByDescending(z => z.MotionConfidence))
        {
            var (dist, bearing) = ZonePolar(z.X, z.Y);
            rows.Add(new SpatialBlockZone
            {
                ZoneId = z.ZoneId,
                Name = z.Name,
                NormalizedX = z.X,
                NormalizedY = z.Y,
                RadiusNorm = z.Radius,
                MotionPct = Math.Clamp(z.MotionConfidence * 100.0, 0, 100),
                OccupancyPct = Math.Clamp(z.OccupancyConfidence * 100.0, 0, 100),
                DistanceMeters = dist,
                BearingDeg = bearing
            });
        }

        ApplyBlockZones(rows);
    }

    private void UpdateBlockZonesFromStored(IEnumerable<StoredZone> zones)
    {
        var rows = new List<SpatialBlockZone>();
        foreach (var z in zones.OrderByDescending(z => z.MotionConfidence))
        {
            var (dist, bearing) = ZonePolar(z.X, z.Y);
            rows.Add(new SpatialBlockZone
            {
                ZoneId = z.ZoneId,
                Name = z.Name,
                NormalizedX = z.X,
                NormalizedY = z.Y,
                RadiusNorm = z.Radius,
                MotionPct = Math.Clamp(z.MotionConfidence * 100.0, 0, 100),
                OccupancyPct = Math.Clamp(z.OccupancyConfidence * 100.0, 0, 100),
                DistanceMeters = dist,
                BearingDeg = bearing
            });
        }

        ApplyBlockZones(rows);
    }

    /// <summary>
    /// Converts a zone's normalized [0,1] position to real-world polar (metres/bearing).
    /// Uses the fixed <see cref="MaxViewportMeters"/> reference span — the SAME scale
    /// <see cref="GetPosition"/> uses to encode AP positions via MetersPolarToNormalized —
    /// so the reported distance reflects the calibrated physical position, not the
    /// live zoom slider. Using the live ViewportMeters here made the same data point
    /// report a different distance every time the user zoomed (reported as "distance
    /// changed, not the data point").
    /// </summary>
    private (double DistM, double BearingDeg) ZonePolar(double nx, double ny)
    {
        double east = (nx - 0.5) * 2.0 * MaxViewportMeters;
        double north = (ny - 0.5) * 2.0 * MaxViewportMeters;
        double dist = Math.Sqrt(east * east + north * north);
        double bearing = BearingStoreService.NormalizeDeg(Math.Atan2(east, north) * 180.0 / Math.PI);
        return (dist, bearing);
    }

    private void AppendMotionTrails(IEnumerable<SpatialBlockZone> zones)
    {
        foreach (var z in zones)
        {
            bool moved = false;
            if (_lastZonePos.TryGetValue(z.ZoneId, out var last))
            {
                double dx = z.NormalizedX - last.X;
                double dy = z.NormalizedY - last.Y;
                moved = Math.Sqrt(dx * dx + dy * dy) > 0.002;
            }

            if (z.MotionPct >= 3 || moved)
                AddPrecisionTrail(z.ZoneId, z.NormalizedX, z.NormalizedY, (float)Math.Clamp(z.MotionPct / 100.0, 0.04, 1), false);

            _lastZonePos[z.ZoneId] = (z.NormalizedX, z.NormalizedY);
        }

        while (MotionTrails.Count > MaxTrailSamples)
            MotionTrails.RemoveAt(0);
    }

    private void AddPrecisionTrail(string id, double nx, double ny, float strength, bool isObstacle)
    {
        MotionTrails.Add(new MotionTrailSample
        {
            ZoneId = id,
            NormalizedX = nx,
            NormalizedY = ny,
            Strength = strength,
            Age = 0f,
            Sequence = ++_trailSequence,
            IsObstacle = isObstacle
        });
    }

    private void ApplyBlockZones(List<SpatialBlockZone> rows)
    {
        AppendMotionTrails(rows);
        BlockZones.Clear();
        foreach (var r in rows)
            BlockZones.Add(r);
        BlockZonesUpdated?.Invoke();
    }

    private void BuildActivitySplatsFromLive(EnvironmentStateMsg state)
    {
        _activitySplats.Clear();
        foreach (var z in state.Zones)
            _activitySplats.Add((z.ZoneId, z.X, z.Y, Math.Max(z.MotionConfidence, z.OccupancyConfidence * 0.5)));

        foreach (var s in state.Signals)
        {
            double motion = Math.Max(s.MotionConfidence, s.Variance / 6.0);
            if (motion < 0.05) continue;
            var (x, y) = GetPosition(s.Bssid);
            _activitySplats.Add((s.Bssid, x, y, motion));
        }
    }

    private void BuildActivitySplatsFromHistory(ReplayFrame frame)
    {
        _activitySplats.Clear();
        foreach (var z in frame.Zones)
            _activitySplats.Add((z.ZoneId, z.X, z.Y, Math.Max(z.MotionConfidence, z.OccupancyConfidence * 0.5)));

        foreach (var ap in frame.ApFeatures)
        {
            double motion = Math.Max(ap.MotionConfidence, ap.Variance / 6.0);
            if (motion < 0.05) continue;
            var (x, y) = GetPosition(ap.Bssid);
            _activitySplats.Add((ap.Bssid, x, y, motion));
        }
    }

    public async Task LoadHistoryAsync(Action<int, double>? onChannelRssi = null)
    {
        if (_grpc.Client == null || _timeRange.IsLiveMode == false) return;
        long fromMs = DateTimeOffset.UtcNow.AddMinutes(-Math.Min(_timeRange.RangeMinutes, 60)).ToUnixTimeMilliseconds();
        long toMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await LoadChannelHistoryRangeAsync(fromMs, toMs, onChannelRssi);
    }

    public async Task LoadChannelHistoryRangeAsync(long fromMs, long toMs, Action<int, double>? onChannelRssi = null)
    {
        if (onChannelRssi == null) return;
        var points = await _data.GetChannelHistoryAsync(fromMs, toMs, 8000);
        foreach (var (_, channel, rssi) in points)
            onChannelRssi(channel, rssi);
        OnChannelHistoryReady?.Invoke();
    }

    [RelayCommand]
    private async Task RefreshPositionsAsync() => await LoadRouterPositionsAsync();

    [RelayCommand]
    private async Task ReloadTimelineAsync() => await _timeline.ReloadAsync();

    [RelayCommand]
    private async Task SetLiveModeAsync()
    {
        _timeRange.SetLiveCommand.Execute(null);
        await _timeline.ReloadAsync();
        await LoadHistoryAsync();
    }

    [RelayCommand]
    private async Task SetHistoricalModeAsync()
    {
        _timeRange.SetHistoricalCommand.Execute(null);
        await _timeline.ReloadAsync();
    }

    private async Task SubscribeToMeasurements()
    {
        if (_grpc.Client == null) return;
        try
        {
            var call = _grpc.Client.StreamMeasurements(new StreamRequest { MinIntervalMs = 500 });
            await foreach (var batch in call.ResponseStream.ReadAllAsync())
            {
                if (!_timeRange.IsLiveMode) continue;
                OnDataReceived?.Invoke(batch);
            }
        }
        catch { }
    }
}
