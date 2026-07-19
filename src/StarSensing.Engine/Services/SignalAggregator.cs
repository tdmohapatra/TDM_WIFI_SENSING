using System.Threading.Channels;
using StarSensing.Core.Models;

namespace StarSensing.Engine.Services;

public class SignalAggregator
{
    private readonly Channel<ScanBatch> _channel;
    private readonly ILogger<SignalAggregator> _logger;
    private ScanBatch? _latestBatch;

    public SignalAggregator(ILogger<SignalAggregator> logger)
    {
        _logger = logger;
        _channel = Channel.CreateBounded<ScanBatch>(new BoundedChannelOptions(100)
        {
            FullMode = BoundedChannelFullMode.DropOldest
        });
    }

    public async ValueTask PublishBatchAsync(ScanBatch batch, CancellationToken ct = default)
    {
        _latestBatch = batch;
        await _channel.Writer.WriteAsync(batch, ct);
    }

    public IAsyncEnumerable<ScanBatch> SubscribeAsync(CancellationToken ct = default)
    {
        return _channel.Reader.ReadAllAsync(ct);
    }

    public ScanBatch? GetLatestBatch() => _latestBatch;
}
