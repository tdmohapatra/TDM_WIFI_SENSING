using System.Collections.ObjectModel;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Grpc.Core;
using Google.Protobuf.WellKnownTypes;
using StarSensing.Core.Protos;
using StarSensing.Dashboard.Models;
using StarSensing.Dashboard.Services;

namespace StarSensing.Dashboard.ViewModels;

/// <summary>One network's time/RSSI series for the SolidView 3D waveform.</summary>
public sealed class SolidSeries
{
    public string Ssid = "";
    public string Bssid = "";
    public string ColorHex = "#00f5d4";
    // Points are (unix ms, rssi dBm) in ascending time.
    public List<(long Ms, int Rssi)> Points = new();
}

/// <summary>
/// Captures selected Wi-Fi signals over a chosen time window (100 ms .. 10 min) from
/// both the live stream and stored history, for the 3D waveform SolidView.
/// </summary>
public partial class SolidViewViewModel : ObservableObject
{
    private readonly GrpcClientService _grpc;
    private readonly NetworkFilterManager _filterManager;
    private readonly EnvironmentStreamService? _envStream;
    private CancellationTokenSource? _streamCts;

    // Per-BSSID rolling sample buffers (UI-thread only).
    private readonly Dictionary<string, List<(long Ms, int Rssi)>> _buffers = new();
    private const long MaxBufferMs = 10 * 60 * 1000;     // keep up to 10 minutes

    public ObservableCollection<SelectableNetwork> Networks => _filterManager.Networks;
    public ObservableCollection<SelectableNetwork> SelectedNetworks => _filterManager.SelectedNetworks;

    [ObservableProperty] private double _windowSeconds = 30;
    [ObservableProperty] private string _statusText = "Live capturing... select networks in Signal Monitor.";
    [ObservableProperty] private int _savedImageCount;
    [ObservableProperty] private string _mlSummary = "ML: waiting for environment stream...";

    public string WindowLabel => WindowSeconds < 1
        ? $"{WindowSeconds * 1000:F0} ms"
        : WindowSeconds < 60 ? $"{WindowSeconds:F0} s" : $"{WindowSeconds / 60:F0} min";

    partial void OnWindowSecondsChanged(double value) => OnPropertyChanged(nameof(WindowLabel));

    public SolidViewViewModel(GrpcClientService grpc, NetworkFilterManager filterManager)
        : this(grpc, filterManager, null)
    {
    }

    public SolidViewViewModel(GrpcClientService grpc, NetworkFilterManager filterManager, EnvironmentStreamService? envStream)
    {
        _grpc = grpc;
        _filterManager = filterManager;
        _envStream = envStream;
        if (_envStream != null)
            _envStream.StateReceived += OnEnvState;
        RestartStream();
    }

    private void OnEnvState(EnvironmentStateMsg state)
    {
        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            MlSummary = $"ML: {state.Classification} · motion {state.MotionConfidence * 100:F0}% · LSTM {state.LstmMotionConfidence * 100:F0}% · CNN {state.CnnActivityScore * 100:F0}% · {state.Zones.Count} zones";
        }, DispatcherPriority.Background);
    }

    // ── Timeframe presets (100 ms .. 10 min) ───────────────────────────
    [RelayCommand] private void Window100ms() => WindowSeconds = 0.1;
    [RelayCommand] private void Window1s() => WindowSeconds = 1;
    [RelayCommand] private void Window10s() => WindowSeconds = 10;
    [RelayCommand] private void Window30s() => WindowSeconds = 30;
    [RelayCommand] private void Window1m() => WindowSeconds = 60;
    [RelayCommand] private void Window5m() => WindowSeconds = 300;
    [RelayCommand] private void Window10m() => WindowSeconds = 600;

    // ── Live stream ────────────────────────────────────────────────────
    private void RestartStream()
    {
        _streamCts?.Cancel();
        _streamCts = new CancellationTokenSource();
        _ = SubscribeAsync(_streamCts.Token);
    }

    private async Task SubscribeAsync(CancellationToken ct)
    {
        if (_grpc.Client == null) return;
        try
        {
            var call = _grpc.Client.StreamMeasurements(new StreamRequest { MinIntervalMs = 100 }, cancellationToken: ct);
            await foreach (var batch in call.ResponseStream.ReadAllAsync(ct))
            {
                long ms = batch.Timestamp?.ToDateTimeOffset().ToUnixTimeMilliseconds()
                          ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                Application.Current.Dispatcher.BeginInvoke(() =>
                {
                    foreach (var m in batch.Measurements)
                        Append(m.Bssid, ms, m.RssiDbm);
                }, DispatcherPriority.Background);
            }
        }
        catch (OperationCanceledException) { }
        catch { }
    }

    private void Append(string bssid, long ms, int rssi)
    {
        if (!_buffers.TryGetValue(bssid, out var list))
        {
            list = new List<(long, int)>();
            _buffers[bssid] = list;
        }
        list.Add((ms, rssi));

        long cutoff = ms - MaxBufferMs;
        if (list.Count > 4 && list[0].Ms < cutoff)
            list.RemoveAll(p => p.Ms < cutoff);
    }

    // ── History backfill ───────────────────────────────────────────────
    [RelayCommand]
    private async Task LoadHistory()
    {
        if (_grpc.Client == null) return;
        var targets = SelectedNetworks.Where(n => n.IsSelected).ToList();
        if (targets.Count == 0)
        {
            StatusText = "Select networks first to load their history.";
            return;
        }

        StatusText = "Loading history...";
        try
        {
            var from = Timestamp.FromDateTime(DateTime.UtcNow.AddSeconds(-WindowSeconds));
            int loaded = 0;
            foreach (var n in targets)
            {
                var resp = await _grpc.Client.GetHistoryAsync(new HistoryRequest
                {
                    Bssid = n.Bssid,
                    From = from,
                    MaxPoints = 2000
                });

                Application.Current.Dispatcher.Invoke(() =>
                {
                    foreach (var m in resp.Measurements)
                        Append(m.Bssid, m.Timestamp?.ToDateTimeOffset().ToUnixTimeMilliseconds()
                            ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), m.RssiDbm);
                });

                // Keep buffers time-sorted after merging history.
                if (_buffers.TryGetValue(n.Bssid, out var list))
                    list.Sort((a, b) => a.Ms.CompareTo(b.Ms));

                loaded += resp.Measurements.Count;
            }
            StatusText = $"Loaded {loaded} historical points across {targets.Count} networks.";
        }
        catch (Exception ex)
        {
            StatusText = $"History load failed: {ex.Message}";
        }
    }

    /// <summary>Snapshot of the selected networks' samples within the current window.</summary>
    public List<SolidSeries> GetSeries()
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        long from = now - (long)(WindowSeconds * 1000);

        var selected = SelectedNetworks.Where(n => n.IsSelected).ToList();
        var result = new List<SolidSeries>();

        foreach (var n in selected)
        {
            if (!_buffers.TryGetValue(n.Bssid, out var list)) continue;
            var pts = list.Where(p => p.Ms >= from).ToList();
            if (pts.Count == 0) continue;

            result.Add(new SolidSeries
            {
                Ssid = n.Ssid,
                Bssid = n.Bssid,
                ColorHex = n.ChartColorHex,
                Points = pts
            });
        }
        return result;
    }

    // ── Copy selected data to clipboard ────────────────────────────────
    [RelayCommand]
    private void CopyData()
    {
        var series = GetSeries();
        if (series.Count == 0)
        {
            StatusText = "Nothing to copy. Select networks with data.";
            return;
        }

        var sb = new StringBuilder();
        sb.AppendLine("SSID,BSSID,TimestampMs,UtcTime,RSSI_dBm");
        foreach (var s in series)
            foreach (var p in s.Points)
                sb.AppendLine($"{s.Ssid},{s.Bssid},{p.Ms},{DateTimeOffset.FromUnixTimeMilliseconds(p.Ms):o},{p.Rssi}");

        try
        {
            Clipboard.SetText(sb.ToString());
            StatusText = $"Copied {series.Sum(s => s.Points.Count)} samples to clipboard.";
        }
        catch (Exception ex)
        {
            StatusText = $"Copy failed: {ex.Message}";
        }
    }
}
