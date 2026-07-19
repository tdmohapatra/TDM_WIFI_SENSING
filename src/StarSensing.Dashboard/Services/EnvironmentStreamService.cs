using System.Windows;
using System.Windows.Threading;
using Grpc.Core;
using StarSensing.Core.Protos;

namespace StarSensing.Dashboard.Services;

/// <summary>Single shared gRPC environment stream — updates network ML metrics for all tabs.</summary>
public sealed class EnvironmentStreamService
{
    private readonly NetworkFilterManager _filter;
    private CancellationTokenSource? _cts;
    private int _intervalMs = 100;

    public event Action<EnvironmentStateMsg>? StateReceived;

    public EnvironmentStreamService(NetworkFilterManager filter) => _filter = filter;

    public void SetIntervalMs(int ms) => _intervalMs = Math.Max(50, ms);

    public void Start(GrpcClientService grpc)
    {
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        _ = RunAsync(grpc, _cts.Token);
    }

    public void Stop() => _cts?.Cancel();

    private async Task RunAsync(GrpcClientService grpc, CancellationToken ct)
    {
        if (grpc.Client == null) return;
        try
        {
            var call = grpc.Client.StreamEnvironmentState(
                new StreamRequest { MinIntervalMs = _intervalMs },
                cancellationToken: ct);

            await foreach (var state in call.ResponseStream.ReadAllAsync(ct))
            {
                Application.Current.Dispatcher.BeginInvoke(() =>
                {
                    foreach (var s in state.Signals)
                        _filter.UpdateProcessedSignal(s);
                    StateReceived?.Invoke(state);
                }, DispatcherPriority.Background);
            }
        }
        catch (OperationCanceledException) { }
        catch { }
    }
}
