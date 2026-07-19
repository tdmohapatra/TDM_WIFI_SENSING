using CommunityToolkit.Mvvm.ComponentModel;
using Grpc.Core;
using StarSensing.Core.Protos;
using StarSensing.Dashboard.Models;
using StarSensing.Dashboard.Services;
using System.Collections.ObjectModel;

namespace StarSensing.Dashboard.ViewModels;

public partial class RadarViewModel : ObservableObject
{
    private readonly NetworkFilterManager _filter;
    private readonly HashSet<string> _knownBssids = new(StringComparer.OrdinalIgnoreCase);

    public event Action<MeasurementBatch>? OnDataReceived;
    public event Action<EnvironmentStateMsg>? OnStateReceived;

    [ObservableProperty] private int _targetCount;
    [ObservableProperty] private double _peakVariance;
    [ObservableProperty] private string _statusText = "Live radar sweep";

    public ObservableCollection<SelectableNetwork> Networks => _filter.Networks;

    public RadarViewModel(GrpcClientService grpc, NetworkFilterManager filter, EnvironmentStreamService envStream)
    {
        _filter = filter;
        _ = SubscribeToMeasurements(grpc);
        envStream.StateReceived += state =>
        {
            PeakVariance = state.Signals.Count > 0 ? state.Signals.Max(s => s.Variance) : 0;
            TargetCount = state.ActiveApCount;
            OnStateReceived?.Invoke(state);
        };
    }

    private async Task SubscribeToMeasurements(GrpcClientService grpc)
    {
        if (grpc.Client == null) return;
        try
        {
            var call = grpc.Client.StreamMeasurements(new StreamRequest { MinIntervalMs = 200 });
            await foreach (var batch in call.ResponseStream.ReadAllAsync())
            {
                int newCount = 0;
                foreach (var m in batch.Measurements)
                {
                    if (_knownBssids.Add(m.Bssid))
                        newCount++;
                }
                if (newCount > 0)
                {
                    SoundService.NewSignal();
                    StatusText = $"New signal detected (+{newCount})";
                }
                OnDataReceived?.Invoke(batch);
            }
        }
        catch { }
    }
}
