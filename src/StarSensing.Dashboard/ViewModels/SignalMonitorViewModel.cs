using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StarSensing.Core.Protos;
using Grpc.Core;
using System.Windows;
using System.Windows.Threading;
using StarSensing.Dashboard.Models;
using StarSensing.Dashboard.Services;
using Google.Protobuf.WellKnownTypes;
using ProtoEmpty = StarSensing.Core.Protos.Empty;

namespace StarSensing.Dashboard.ViewModels;

public partial class SignalMonitorViewModel : ObservableObject
{
    private readonly GrpcClientService _grpc = null!;
    private CancellationTokenSource? _measurementStreamCts;

    // ── Filter manager drives the Network list ─────────────────────────
    private readonly NetworkFilterManager _filterManager = new();

    /// <summary>Every identified network (used by the History tab).</summary>
    public ObservableCollection<SelectableNetwork> Networks => _filterManager.Networks;

    /// <summary>Only currently-online, unique networks (used by the Live tab); pruned every 2s.</summary>
    public ObservableCollection<SelectableNetwork> LiveNetworks { get; } = new();

    /// <summary>
    /// A network is considered online if seen within this many seconds. Windows only
    /// refreshes Wi-Fi scans every few seconds, so this must be generous enough that
    /// rows don't vanish between scans — only genuinely-gone devices are removed.
    /// </summary>
    private const double OnlineWindowSeconds = 18.0;

    private readonly DispatcherTimer _pruneTimer;

    public event Action<MeasurementBatch>? OnDataReceived;

    [ObservableProperty]
    private int _networkCount;

    [ObservableProperty]
    private int _onlineCount;

    [ObservableProperty]
    private int _offlineCount;

    [ObservableProperty]
    private int _refreshCount;

    [ObservableProperty]
    private string _uptimeText = "00:00:00";

    [ObservableProperty]
    private string _lastRefreshText = "-";

    [ObservableProperty]
    private int _secondsToRefresh = 2;

    private readonly DateTimeOffset _startedAt = DateTimeOffset.UtcNow;
    private readonly DispatcherTimer _uiTimer;
    private bool _firstPruneDone;
    private bool _measurementStreamActive;

    // ── Save-to-location ───────────────────────────────────────────────
    private readonly LocationStoreService _locationStore = new();
    private readonly BearingStoreService? _bearingStore;
    private readonly MapImageService _mapImages = new();

    [ObservableProperty]
    private string _locationName = string.Empty;

    [ObservableProperty]
    private string _saveStatusText = "Select networks and a name, then Save Location.";

    [ObservableProperty]
    private bool _isSaving;

    [ObservableProperty]
    private int _sampleIntervalMs = 100;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private bool _showActiveOnly = true;

    [ObservableProperty]
    private DateTimeOffset? _lastBatchAt;

    [ObservableProperty]
    private bool _showChartMapBackground;

    [ObservableProperty]
    private double _chartMapOpacity = 0.25;

    [ObservableProperty]
    private bool _isChartVisible = true;

    [ObservableProperty]
    private bool _isLiveVisible = true;

    [ObservableProperty]
    private bool _isHistoryVisible = true;

    [ObservableProperty]
    private double _motionConfidencePct;

    [ObservableProperty]
    private double _stabilityPct = 100;

    [ObservableProperty]
    private int _anomalyCount;

    [ObservableProperty]
    private double _avgCrossCorrelation;

    [ObservableProperty]
    private int _activeZoneCount;

    [ObservableProperty]
    private double _lstmMotionPct;

    [ObservableProperty]
    private double _cnnActivityPct;

    // ── Constructors ───────────────────────────────────────────────────
    public SignalMonitorViewModel()
    {
        _pruneTimer = CreatePruneTimer();
        _uiTimer = CreateUiTimer();
    }

    public SignalMonitorViewModel(GrpcClientService grpc)
        : this(grpc, new NetworkFilterManager())
    {
    }

    public SignalMonitorViewModel(GrpcClientService grpc, NetworkFilterManager filterManager)
        : this(grpc, filterManager, null)
    {
    }

    public SignalMonitorViewModel(GrpcClientService grpc, NetworkFilterManager filterManager, EnvironmentStreamService? envStream)
        : this(grpc, filterManager, envStream, null)
    {
    }

    public SignalMonitorViewModel(GrpcClientService grpc, NetworkFilterManager filterManager, EnvironmentStreamService? envStream, BearingStoreService? bearingStore)
    {
        _grpc = grpc;
        _filterManager = filterManager;
        _bearingStore = bearingStore;
        _pruneTimer = CreatePruneTimer();
        _uiTimer = CreateUiTimer();
        RestartMeasurements();
        if (envStream != null)
            envStream.StateReceived += OnEnvironmentState;
    }

    private void OnEnvironmentState(EnvironmentStateMsg state)
    {
        MotionConfidencePct = state.MotionConfidence * 100.0;
        StabilityPct = state.StabilityIndex * 100.0;
        AnomalyCount = state.Signals.Count(s => s.IsAnomaly);
        AvgCrossCorrelation = state.Signals.Count > 0
            ? state.Signals.Average(s => s.CrossCorrelation) : 0;
        ActiveZoneCount = state.Zones.Count;
        LstmMotionPct = state.LstmMotionConfidence * 100.0;
        CnnActivityPct = state.CnnActivityScore * 100.0;
    }

    private DispatcherTimer CreateUiTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) =>
        {
            var up = DateTimeOffset.UtcNow - _startedAt;
            UptimeText = $"{(int)up.TotalHours:D2}:{up.Minutes:D2}:{up.Seconds:D2}";
            if (SecondsToRefresh > 0) SecondsToRefresh--;
        };
        timer.Start();
        return timer;
    }

    private DispatcherTimer CreatePruneTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        timer.Tick += (_, _) => RebuildLiveNetworks();
        timer.Start();
        return timer;
    }

    /// <summary>
    /// Background prune (every 2s): removes only the networks that went inactive and
    /// appends newly-seen ones. Existing rows keep their position so the list never
    /// "reloads" or jumps around.
    /// </summary>
    private void RebuildLiveNetworks()
    {
        var now = DateTimeOffset.UtcNow;

        bool IsOnline(SelectableNetwork n) =>
            n.LastSeen != null && (now - n.LastSeen.Value).TotalSeconds <= OnlineWindowSeconds;

        // 1) Remove only the entries that are no longer active.
        for (int i = LiveNetworks.Count - 1; i >= 0; i--)
        {
            if (!IsOnline(LiveNetworks[i]))
                LiveNetworks.RemoveAt(i);
        }

        // 2) Append newly-online networks that aren't in the list yet (stable order).
        int newlyOnline = 0;
        bool weakArrival = false;
        foreach (var n in Networks)
        {
            if (IsOnline(n) && !LiveNetworks.Contains(n))
            {
                LiveNetworks.Add(n);
                newlyOnline++;
                if (n.LatestRssi <= -80) weakArrival = true;
            }
        }

        // Sound cues (skip the very first populate so we don't blast on startup).
        if (_firstPruneDone && newlyOnline > 0)
        {
            if (weakArrival) SoundService.Alert();
            else SoundService.NewSignal();
        }
        _firstPruneDone = true;

        OnlineCount = LiveNetworks.Count;
        NetworkCount = Networks.Count;
        OfflineCount = Math.Max(0, NetworkCount - OnlineCount);
        RefreshCount++;
        LastRefreshText = DateTimeOffset.Now.ToString("HH:mm:ss");
        SecondsToRefresh = 2;
    }

    // ── Commands ───────────────────────────────────────────────────────
    [RelayCommand]
    private void SelectAll()
    {
        foreach (var n in Networks) n.IsSelected = true;
    }

    [RelayCommand]
    private void UnselectAll()
    {
        foreach (var n in Networks) n.IsSelected = false;
    }

    /// <summary>
    /// Captures the current location (approx. lat/lon by IP) and saves all selected
    /// networks under a new location record (with a generated id) in SQL Server.
    /// </summary>
    [RelayCommand]
    private async Task SaveLocationAsync()
    {
        if (IsSaving) return;

        var selected = Networks.Where(n => n.IsSelected).ToList();
        if (selected.Count == 0)
        {
            SaveStatusText = "No networks selected. Tick networks first (or use Select All).";
            return;
        }

        var name = string.IsNullOrWhiteSpace(LocationName)
            ? $"Location {DateTime.Now:yyyy-MM-dd HH:mm}"
            : LocationName.Trim();

        IsSaving = true;
        SaveStatusText = $"Saving {selected.Count} signals under '{name}'...";
        try
        {
            double? lat = null, lon = null;
            try
            {
                var loc = await _mapImages.GetIpLocationAsync();
                if (loc != null) { lat = loc.Value.Lat; lon = loc.Value.Lon; }
            }
            catch { /* location is optional */ }

            var id = await _locationStore.SaveLocationAsync(name, lat, lon, selected);

            if (_bearingStore != null)
            {
                foreach (var n in selected)
                    await _bearingStore.SetBearingAsync(n.Bssid, n.BearingDegrees, "location");
            }

            string coords = lat != null ? $" @ {lat:F4},{lon:F4}" : "";
            SaveStatusText = $"Saved {selected.Count} signals under '{name}'{coords}  (id {id.ToString()[..8]})";
        }
        catch (Exception ex)
        {
            SaveStatusText = $"Save failed: {ex.Message}";
        }
        finally
        {
            IsSaving = false;
        }
    }

    /// <summary>Manual refresh: re-runs the prune immediately.</summary>
    [RelayCommand]
    private void Refresh() => RebuildLiveNetworks();

    /// <summary>
    /// Smooth reload: keeps every existing detail in place. It only (re)starts the live
    /// stream if it actually stopped, reloads history additively, and prunes just the
    /// rows that are no longer active — it never clears the whole list.
    /// </summary>
    [RelayCommand]
    private void ReloadPage()
    {
        if (!_measurementStreamActive)
            RestartMeasurements();

        _ = LoadHistoryAsync();
        RebuildLiveNetworks();
    }

    [RelayCommand]
    private void ToggleChartVisibility() => IsChartVisible = !IsChartVisible;

    [RelayCommand]
    private void ToggleLiveVisibility() => IsLiveVisible = !IsLiveVisible;

    [RelayCommand]
    private void ToggleHistoryVisibility() => IsHistoryVisible = !IsHistoryVisible;

    /// <summary>
    /// Returns the SelectableNetwork entry for the given BSSID (for color lookup in code-behind).
    /// </summary>
    public SelectableNetwork? GetNetwork(string bssid) => _filterManager.GetNetwork(bssid);

    /// <summary>Text match across the key fields. Used by both Live and History views.</summary>
    public bool MatchesSearch(SelectableNetwork n)
    {
        if (string.IsNullOrWhiteSpace(SearchText))
            return true;

        var q = SearchText.Trim();
        return n.Ssid.Contains(q, StringComparison.OrdinalIgnoreCase)
               || n.Bssid.Contains(q, StringComparison.OrdinalIgnoreCase)
               || n.Band.Contains(q, StringComparison.OrdinalIgnoreCase)
               || n.BandChannelText.Contains(q, StringComparison.OrdinalIgnoreCase)
               || n.DirectionText.Contains(q, StringComparison.OrdinalIgnoreCase)
               || n.ModelText.Contains(q, StringComparison.OrdinalIgnoreCase)
               || n.DeviceIdText.Contains(q, StringComparison.OrdinalIgnoreCase);
    }

    public string FrameInfoText => LastBatchAt?.ToLocalTime().ToString("HH:mm:ss.fff") is { } ts
        ? $"{SampleIntervalMs}ms frame | last {ts}"
        : $"{SampleIntervalMs}ms frame | waiting...";

    partial void OnSampleIntervalMsChanged(int value)
    {
        OnPropertyChanged(nameof(FrameInfoText));
        RestartMeasurements();
    }

    partial void OnSearchTextChanged(string value) => OnPropertyChanged(nameof(FrameInfoText));

    partial void OnShowActiveOnlyChanged(bool value) => OnPropertyChanged(nameof(FrameInfoText));

    partial void OnLastBatchAtChanged(DateTimeOffset? value) => OnPropertyChanged(nameof(FrameInfoText));

    // ── History pre-population ─────────────────────────────────────────
    /// <summary>
    /// Loads the last 60 s of history for the top 20 strongest networks so the
    /// chart starts populated immediately on connect.
    /// </summary>
    public async Task LoadHistoryAsync()
    {
        if (_grpc.Client == null) return;
        try
        {
            var networks = await _grpc.Client.GetCurrentNetworksAsync(new ProtoEmpty());
            var from = Timestamp.FromDateTime(DateTime.UtcNow.AddSeconds(-60));

            foreach (var net in networks.Networks.OrderByDescending(n => n.RssiDbm).Take(20))
            {
                var history = await _grpc.Client.GetHistoryAsync(new HistoryRequest
                {
                    Bssid     = net.Bssid,
                    From      = from,
                    MaxPoints = 100
                });

                if (history.Measurements.Count == 0) continue;

                var batch = new MeasurementBatch();
                batch.Measurements.AddRange(history.Measurements);

                Application.Current.Dispatcher.Invoke(() => OnDataReceived?.Invoke(batch));
            }
        }
        catch { }
    }

    // ── Live stream ────────────────────────────────────────────────────
    private void RestartMeasurements()
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
            _measurementStreamActive = true;
            var call = _grpc.Client.StreamMeasurements(
                new StreamRequest { MinIntervalMs = SampleIntervalMs },
                cancellationToken: ct);

            await foreach (var batch in call.ResponseStream.ReadAllAsync(ct))
            {
                Application.Current.Dispatcher.BeginInvoke(() =>
                {
                    // Update filter manager — preserves IsSelected state
                    foreach (var m in batch.Measurements)
                    {
                        string band = m.FrequencyKhz is > 5000000 ? "5 GHz" :
                                      m.FrequencyKhz is > 0       ? "2.4 GHz" : "";

                        _filterManager.UpdateNetwork(m.Bssid, m.Ssid, m.RssiDbm, m.Channel, band, m.SignalQuality, m.FrequencyKhz);
                    }

                    NetworkCount = Networks.Count;
                    LastBatchAt = batch.Timestamp?.ToDateTimeOffset() ?? DateTimeOffset.UtcNow;
                    OnDataReceived?.Invoke(batch);
                }, DispatcherPriority.Background);
            }
        }
        catch (OperationCanceledException) { }
        catch { }
        finally
        {
            // Only mark inactive if this is still the current stream (avoids a
            // restart's cancelled task flipping the flag for the new one).
            if (_measurementStreamCts?.Token == ct)
                _measurementStreamActive = false;
        }
    }
}
