using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using StarSensing.Core.Interfaces;
using StarSensing.Core.Models;
using StarSensing.Core.Protos;
using Empty = StarSensing.Core.Protos.Empty;

namespace StarSensing.Engine.Services;

public class GrpcSensingService : SensingService.SensingServiceBase
{
    private readonly IWiFiScanner _scanner;
    private readonly ISignalStore _store;
    private readonly IMotionDetector _detector;
    private readonly ISignalProcessor _processor;
    private readonly SignalAggregator _aggregator;
    private readonly ILogger<GrpcSensingService> _logger;

    public GrpcSensingService(
        IWiFiScanner scanner,
        ISignalStore store,
        IMotionDetector detector,
        ISignalProcessor processor,
        SignalAggregator aggregator,
        ILogger<GrpcSensingService> logger)
    {
        _scanner = scanner;
        _store = store;
        _detector = detector;
        _processor = processor;
        _aggregator = aggregator;
        _logger = logger;
    }

    public override async Task StreamMeasurements(StreamRequest request, IServerStreamWriter<MeasurementBatch> responseStream, ServerCallContext context)
    {
        _logger.LogInformation(
            "Client connected to StreamMeasurements. Peer={Peer}, MinIntervalMs={MinIntervalMs}",
            context.Peer,
            request.MinIntervalMs);
        try
        {
            await foreach (var batch in _aggregator.SubscribeAsync(context.CancellationToken))
            {
                var msg = new MeasurementBatch
                {
                    BatchId = batch.BatchId.ToString(),
                    Timestamp = Timestamp.FromDateTimeOffset(batch.Timestamp),
                    ScanDurationMs = batch.ScanDurationMs
                };
                foreach (var m in batch.Measurements)
                {
                    msg.Measurements.Add(new SignalMeasurementMsg
                    {
                        Bssid = m.Bssid,
                        Ssid = m.Ssid,
                        RssiDbm = m.RssiDbm,
                        SignalQuality = m.SignalQuality,
                        Channel = m.Channel,
                        FrequencyKhz = m.FrequencyKHz,
                        Timestamp = Timestamp.FromDateTimeOffset(m.Timestamp)
                    });
                }
                await responseStream.WriteAsync(msg);
                
                if (request.MinIntervalMs > 0)
                {
                    await Task.Delay(request.MinIntervalMs, context.CancellationToken);
                }
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Client disconnected from StreamMeasurements. Peer={Peer}", context.Peer);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "StreamMeasurements failed. Peer={Peer}", context.Peer);
            throw;
        }
    }

    public override async Task StreamEnvironmentState(StreamRequest request, IServerStreamWriter<EnvironmentStateMsg> responseStream, ServerCallContext context)
    {
        _logger.LogInformation(
            "Client connected to StreamEnvironmentState. Peer={Peer}, MinIntervalMs={MinIntervalMs}",
            context.Peer,
            request.MinIntervalMs);
        try
        {
            await foreach (var batch in _aggregator.SubscribeAsync(context.CancellationToken))
            {
                var state = await _detector.AnalyzeAsync(batch, context.CancellationToken);
                var msg = new EnvironmentStateMsg
                {
                    Timestamp = Timestamp.FromDateTimeOffset(state.Timestamp),
                    MotionConfidence = state.MotionConfidence,
                    OccupancyConfidence = state.OccupancyConfidence,
                    Classification = (EnvironmentClass)state.Classification,
                    ActiveApCount = state.ActiveApCount,
                    StabilityIndex = state.StabilityIndex,
                    LstmMotionConfidence = state.LstmMotionConfidence,
                    CnnActivityScore = state.CnnActivityScore
                };
                
                foreach (var sig in state.Signals)
                {
                    msg.Signals.Add(new ProcessedSignalMsg
                    {
                        Bssid = sig.Bssid,
                        Ssid = sig.Ssid,
                        RawRssi = sig.RawRssi,
                        SmoothedRssi = sig.SmoothedRssi,
                        Variance = sig.Variance,
                        StdDev = sig.StdDev,
                        ChangeRate = sig.ChangeRate,
                        DominantFrequency = sig.DominantFrequency,
                        SpectralEnergy = sig.SpectralEnergy,
                        ZScore = sig.ZScore,
                        IsAnomaly = sig.IsAnomaly,
                        MotionConfidence = sig.MotionConfidence,
                        Entropy = sig.Entropy,
                        CrossCorrelation = sig.CrossCorrelation
                    });
                }

                foreach (var z in state.Zones)
                {
                    msg.Zones.Add(new SpatialZoneMsg
                    {
                        ZoneId = z.ZoneId,
                        Name = z.Name,
                        X = z.X,
                        Y = z.Y,
                        Radius = z.Radius,
                        OccupancyConfidence = z.OccupancyConfidence,
                        MotionConfidence = z.MotionConfidence,
                        Color = z.Color
                    });
                }

                foreach (var evt in state.ActiveEvents)
                {
                    var eventMsg = new MotionEventMsg
                    {
                        EventId = evt.Id.ToString(),
                        Timestamp = Timestamp.FromDateTimeOffset(evt.Timestamp),
                        EventType = (MotionEventTypeMsg)evt.EventType,
                        Confidence = evt.Confidence,
                        Description = evt.Description,
                        AffectedApCount = evt.AffectedApCount,
                        AverageVariance = evt.AverageVariance,
                        PeakRssiChange = evt.PeakRssiChange,
                        DurationMs = evt.DurationMs
                    };
                    eventMsg.AffectedBssids.AddRange(evt.AffectedBssids);
                    msg.ActiveEvents.Add(eventMsg);
                }

                _logger.LogDebug(
                    "Environment state streamed. Peer={Peer}, Signals={SignalCount}, Events={EventCount}, ActiveAps={ActiveApCount}, MotionConfidence={MotionConfidence:P0}",
                    context.Peer,
                    msg.Signals.Count,
                    msg.ActiveEvents.Count,
                    msg.ActiveApCount,
                    msg.MotionConfidence);

                await responseStream.WriteAsync(msg);
                
                if (request.MinIntervalMs > 0)
                {
                    await Task.Delay(request.MinIntervalMs, context.CancellationToken);
                }
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Client disconnected from StreamEnvironmentState. Peer={Peer}", context.Peer);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "StreamEnvironmentState failed. Peer={Peer}", context.Peer);
            throw;
        }
    }

    public override async Task<NetworkListResponse> GetCurrentNetworks(Empty request, ServerCallContext context)
    {
        var measurements = await _store.GetLatestMeasurementsAsync(60, context.CancellationToken);
        var res = new NetworkListResponse { Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow) };
        foreach (var m in measurements)
        {
            res.Networks.Add(new WiFiNetworkMsg
            {
                Bssid = m.Bssid,
                Ssid = m.Ssid,
                RssiDbm = m.RssiDbm,
                SignalQuality = m.SignalQuality,
                Channel = m.Channel,
                FrequencyKhz = m.FrequencyKHz,
                Band = WiFiNetwork.FrequencyToBand(m.FrequencyKHz),
                LastSeen = Timestamp.FromDateTimeOffset(m.Timestamp)
            });
        }
        _logger.LogInformation("GetCurrentNetworks returned {NetworkCount} networks to {Peer}.", res.Networks.Count, context.Peer);
        return res;
    }

    public override async Task<SavedNetworkListResponse> GetSavedNetworks(SavedNetworkRequest request, ServerCallContext context)
    {
        var networks = await _store.GetSavedAccessPointsAsync(request.MaxAgeSeconds, context.CancellationToken);
        var res = new SavedNetworkListResponse { Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow) };

        foreach (var n in networks)
        {
            res.Networks.Add(new SavedNetworkMsg
            {
                Bssid = n.Bssid,
                Ssid = n.Ssid,
                LatestRssiDbm = n.LatestRssiDbm,
                SignalQuality = n.SignalQuality,
                Channel = n.Channel,
                FrequencyKhz = n.FrequencyKHz,
                Band = n.Band,
                FirstSeen = Timestamp.FromDateTimeOffset(n.FirstSeen),
                LastSeen = Timestamp.FromDateTimeOffset(n.LastSeen),
                MinRssiDbm = n.MinRssiDbm,
                MaxRssiDbm = n.MaxRssiDbm,
                SampleCount = n.SampleCount
            });
        }

        _logger.LogInformation(
            "GetSavedNetworks returned {NetworkCount} networks to {Peer}. MaxAgeSeconds={MaxAgeSeconds}",
            res.Networks.Count,
            context.Peer,
            request.MaxAgeSeconds);

        return res;
    }

    public override async Task<ScanResponse> TriggerScan(Empty request, ServerCallContext context)
    {
        try
        {
            var batch = await _scanner.ScanAsync(context.CancellationToken);
            await _store.StoreBatchAsync(batch, context.CancellationToken);
            await _processor.ProcessAsync(batch, context.CancellationToken);
            await _aggregator.PublishBatchAsync(batch, context.CancellationToken);
            _logger.LogInformation(
                "Manual scan triggered by {Peer}. NetworksFound={NetworksFound}, DurationMs={DurationMs}",
                context.Peer,
                batch.NetworkCount,
                batch.ScanDurationMs);
            return new ScanResponse { Success = true, NetworksFound = batch.NetworkCount, ScanDurationMs = batch.ScanDurationMs };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Manual scan failed. Peer={Peer}", context.Peer);
            return new ScanResponse { Success = false, ErrorMessage = ex.Message };
        }
    }

    public override async Task<Empty> ResetBaseline(Empty request, ServerCallContext context)
    {
        await _detector.ResetBaselineAsync(context.CancellationToken);
        _logger.LogInformation("Motion baseline reset by {Peer}.", context.Peer);
        return new Empty();
    }

    public override async Task<HistoryResponse> GetHistory(HistoryRequest request, ServerCallContext context)
    {
        if (string.IsNullOrWhiteSpace(request.Bssid))
        {
            _logger.LogWarning("GetHistory rejected empty BSSID from {Peer}.", context.Peer);
            return new HistoryResponse();
        }

        DateTimeOffset to = request.To?.ToDateTimeOffset() ?? DateTimeOffset.UtcNow;
        DateTimeOffset from = request.From?.ToDateTimeOffset() ?? to.AddSeconds(-60);

        if (from > to)
        {
            _logger.LogWarning(
                "GetHistory received inverted time range. Peer={Peer}, Bssid={Bssid}, From={From:o}, To={To:o}. Swapping values.",
                context.Peer,
                request.Bssid,
                from,
                to);
            (from, to) = (to, from);
        }

        var measurements = await _store.GetMeasurementsAsync(request.Bssid, from, to, context.CancellationToken);
        if (request.MaxPoints > 0 && measurements.Count > request.MaxPoints)
            measurements = measurements.TakeLast(request.MaxPoints).ToArray();

        var res = new HistoryResponse();
        foreach (var m in measurements)
        {
            res.Measurements.Add(new SignalMeasurementMsg
            {
                Bssid = m.Bssid,
                Ssid = m.Ssid,
                RssiDbm = m.RssiDbm,
                SignalQuality = m.SignalQuality,
                Channel = m.Channel,
                FrequencyKhz = m.FrequencyKHz,
                Timestamp = Timestamp.FromDateTimeOffset(m.Timestamp)
            });
        }

        _logger.LogInformation(
            "GetHistory returned {PointCount} points. Peer={Peer}, Bssid={Bssid}, From={From:o}, To={To:o}, MaxPoints={MaxPoints}",
            res.Measurements.Count,
            context.Peer,
            request.Bssid,
            from,
            to,
            request.MaxPoints);

        return res;
    }
}
