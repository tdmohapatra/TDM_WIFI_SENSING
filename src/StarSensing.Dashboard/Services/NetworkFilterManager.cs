using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StarSensing.Core.Protos;
using StarSensing.Dashboard.Models;

namespace StarSensing.Dashboard.Services;

public partial class NetworkFilterManager : ObservableObject
{
    public ObservableCollection<SelectableNetwork> Networks { get; } = new();
    public ObservableCollection<SelectableNetwork> SelectedNetworks { get; } = new();

    private readonly Dictionary<string, SelectableNetwork> _networkDict = new(StringComparer.OrdinalIgnoreCase);
    private readonly BearingStoreService? _bearings;
    private readonly List<SelectableNetwork> _batchPending = new();
    private int _batchDepth;

    public NetworkFilterManager() { }

    public NetworkFilterManager(BearingStoreService bearings)
    {
        _bearings = bearings;
        _bearings.BearingsChanged += () => RunOnUi(RefreshAllBearings, DispatcherPriority.Background);
    }

    public void BeginBatchImport() => _batchDepth++;

    public void EndBatchImport()
    {
        if (_batchDepth <= 0)
            return;

        _batchDepth--;
        if (_batchDepth > 0)
            return;

        RunOnUi(() =>
        {
            foreach (var net in _batchPending)
                AddNetworkToCollections(net);
            _batchPending.Clear();
        });
    }

    public void RefreshAllBearings()
    {
        foreach (var net in _networkDict.Values)
            ApplyBearing(net);
    }

    private void ApplyBearing(SelectableNetwork net)
    {
        if (_bearings != null && _bearings.TryGetBearing(net.Bssid, out var deg, out var src))
            net.SetBearing(deg, src);
        else
            net.SetBearing(SelectableNetwork.EstimateBearingFromBssid(net.Bssid), "estimated");
    }

    public void UpdateNetwork(string bssid, string ssid, int rssi, int channel = 0, string band = "", int signalQuality = 0, int frequencyKhz = 0)
    {
        var net = GetOrCreateNetwork(bssid, ssid);
        var now = DateTimeOffset.UtcNow;

        net.LatestRssi = rssi;
        net.LastSeen = now;
        if (net.FirstSeen == null)
            net.FirstSeen = now;

        if (channel > 0)
            net.Channel = channel;

        if (!string.IsNullOrEmpty(band))
            net.Band = band;

        if (signalQuality > 0)
            net.SignalQuality = signalQuality;

        if (frequencyKhz > 0)
            net.FrequencyKhz = frequencyKhz;

        if (net.SampleCount <= 0)
        {
            net.SampleCount = 1;
            net.MinRssiDbm = rssi;
            net.MaxRssiDbm = rssi;
        }
        else
        {
            net.SampleCount += 1;
            net.MinRssiDbm = Math.Min(net.MinRssiDbm, rssi);
            net.MaxRssiDbm = Math.Max(net.MaxRssiDbm, rssi);
        }
    }

    public void UpdateSavedNetwork(SavedNetworkMsg network)
    {
        var net = GetOrCreateNetwork(network.Bssid, network.Ssid);

        net.LatestRssi = network.LatestRssiDbm;
        net.SignalQuality = network.SignalQuality;
        net.Channel = network.Channel;
        net.FrequencyKhz = network.FrequencyKhz;
        net.Band = network.Band;
        net.FirstSeen = network.FirstSeen?.ToDateTimeOffset();
        net.LastSeen = network.LastSeen?.ToDateTimeOffset();
        net.MinRssiDbm = network.MinRssiDbm;
        net.MaxRssiDbm = network.MaxRssiDbm;
        net.SampleCount = network.SampleCount;
    }

    public void UpdateMotionMetrics(string bssid, double variance, double motionConfidence)
    {
        if (!_networkDict.TryGetValue(bssid, out var net))
            return;

        net.Variance = variance;
        net.MotionConfidence = motionConfidence;
    }

    public void UpdateProcessedSignal(ProcessedSignalMsg msg)
    {
        var net = GetOrCreateNetwork(msg.Bssid, msg.Ssid);
        net.LatestRssi = msg.RawRssi;
        net.SmoothedRssi = (int)Math.Round(msg.SmoothedRssi);
        net.Variance = msg.Variance;
        net.ChangeRate = msg.ChangeRate;
        net.Entropy = msg.Entropy;
        net.CrossCorrelation = msg.CrossCorrelation;
        net.MotionConfidence = msg.MotionConfidence > 0 ? msg.MotionConfidence : Math.Min(1.0, msg.Variance / 6.0);
        net.IsAnomaly = msg.IsAnomaly;
        net.LastSeen = DateTimeOffset.UtcNow;
    }

    private SelectableNetwork GetOrCreateNetwork(string bssid, string ssid)
    {
        if (_networkDict.TryGetValue(bssid, out var net))
            return net;

        net = new SelectableNetwork(bssid, ssid);
        ApplyBearing(net);
        net.PropertyChanged += Network_PropertyChanged;
        _networkDict[bssid] = net;

        if (_batchDepth > 0)
        {
            _batchPending.Add(net);
            return net;
        }

        RunOnUi(() => AddNetworkToCollections(net));
        return net;
    }

    private void AddNetworkToCollections(SelectableNetwork net)
    {
        if (!Networks.Contains(net))
            Networks.Add(net);

        if (net.IsSelected && !SelectedNetworks.Contains(net))
            SelectedNetworks.Add(net);
    }

    private void Network_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(SelectableNetwork.IsSelected) || sender is not SelectableNetwork net)
            return;

        RunOnUi(() =>
        {
            if (net.IsSelected)
            {
                if (!SelectedNetworks.Contains(net))
                    SelectedNetworks.Add(net);
            }
            else
            {
                SelectedNetworks.Remove(net);
            }
        });
    }

    private static void RunOnUi(Action action, DispatcherPriority priority = DispatcherPriority.Normal)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null)
        {
            action();
            return;
        }

        if (dispatcher.CheckAccess())
            action();
        else
            dispatcher.BeginInvoke(action, priority);
    }

    public SelectableNetwork? GetNetwork(string bssid) =>
        _networkDict.TryGetValue(bssid, out var n) ? n : null;

    public bool IsSelected(string bssid) =>
        _networkDict.TryGetValue(bssid, out var net) && net.IsSelected;

    [RelayCommand]
    private void SelectAll()
    {
        foreach (var net in Networks)
            net.IsSelected = true;
    }

    [RelayCommand]
    private void UnselectAll()
    {
        foreach (var net in Networks)
            net.IsSelected = false;
    }
}
