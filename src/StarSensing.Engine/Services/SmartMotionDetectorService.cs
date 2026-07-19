using Google.Protobuf.WellKnownTypes;
using Grpc.Net.Client;
using StarSensing.Core.Interfaces;
using StarSensing.Core.Models;
using StarSensing.Core.Protos;

namespace StarSensing.Engine.Services;

/// <summary>
/// Primary motion/signal processor: calls Python gRPC for ML features, falls back to
/// local variance detection, persists features/events/zones to SQL Server.
/// </summary>
public sealed class SmartMotionDetectorService : IMotionDetector, ISignalProcessor, IDisposable
{
    private readonly ILogger<SmartMotionDetectorService> _logger;
    private readonly ISignalStore _store;
    private readonly EnvironmentStateCache _cache;
    private readonly MotionDetectorService _fallback;
    private readonly string _pythonAddress;
    private readonly int _processIntervalMs;
    private readonly SemaphoreSlim _processLock = new(1, 1);

    private GrpcChannel? _channel;
    private SignalProcessorService.SignalProcessorServiceClient? _python;
    private bool _pythonAvailable;
    private DateTimeOffset _lastPythonAttempt = DateTimeOffset.MinValue;
    private DateTimeOffset _lastProcessAt = DateTimeOffset.MinValue;
    private readonly List<MotionEvent> _recentEvents = new();
    private double _motionConfidence;

    public double CurrentMotionConfidence => _motionConfidence;
    public IReadOnlyList<MotionEvent> RecentEvents => _recentEvents;

    public SmartMotionDetectorService(
        ILogger<SmartMotionDetectorService> logger,
        ISignalStore store,
        EnvironmentStateCache cache,
        MotionDetectorService fallback,
        IConfiguration config)
    {
        _logger = logger;
        _store = store;
        _cache = cache;
        _fallback = fallback;
        _pythonAddress = config.GetValue<string>("SensingConfig:PythonProcessorAddress")
            ?? "http://localhost:5051";
        _processIntervalMs = config.GetValue<int>("SensingConfig:PythonProcessIntervalMs", 200);
    }

    public EnvironmentState? GetCachedState(Guid batchId) => _cache.Get(batchId);

    public async Task<EnvironmentState> AnalyzeAsync(ScanBatch batch, CancellationToken ct = default)
    {
        var cached = _cache.Get(batch.BatchId);
        if (cached != null)
            return cached;

        return await ProcessAsync(batch, ct);
    }

    public async Task<EnvironmentState> ProcessAsync(ScanBatch batch, CancellationToken ct = default)
    {
        // Throttle Python calls — raw scans still stored every tick by ScanWorker.
        if ((DateTimeOffset.UtcNow - _lastProcessAt).TotalMilliseconds < _processIntervalMs)
        {
            var prev = _cache.Latest;
            if (prev.Signals.Count > 0)
                return prev;
        }

        await _processLock.WaitAsync(ct);
        try
        {
            EnvironmentState state;
            ProcessingResult? pythonResult = null;

            if (TryEnsurePythonClient())
            {
                try
                {
                    var msg = ToMeasurementBatch(batch);
                    pythonResult = await _python!.ProcessBatchAsync(msg, cancellationToken: ct);
                    state = MapFromPython(pythonResult, batch);
                    if (!_pythonAvailable)
                    {
                        _logger.LogInformation("Connected to Python signal processor at {Address}", _pythonAddress);
                    }
                    _pythonAvailable = true;
                }
                catch (Exception ex)
                {
                    _pythonAvailable = false;
                    _logger.LogWarning(ex, "Python processor unavailable — using local fallback.");
                    state = await _fallback.AnalyzeAsync(batch, ct);
                }
            }
            else
            {
                state = await _fallback.AnalyzeAsync(batch, ct);
            }

            _motionConfidence = state.MotionConfidence;
            MergeEvents(state);
            _cache.Put(batch.BatchId, state);
            _lastProcessAt = DateTimeOffset.UtcNow;

            await PersistAsync(batch, state, pythonResult != null, ct);
            return state;
        }
        finally
        {
            _processLock.Release();
        }
    }

    public async Task ResetBaselineAsync(CancellationToken ct = default)
    {
        await _fallback.ResetBaselineAsync(ct);
        _recentEvents.Clear();
        _motionConfidence = 0;
        _cache.Clear();
        _logger.LogInformation("Smart processor baseline reset.");
    }

    private bool TryEnsurePythonClient()
    {
        if (_pythonAvailable && _python != null)
            return true;

        // Retry Python connection every 10 s after failure.
        if ((DateTimeOffset.UtcNow - _lastPythonAttempt).TotalSeconds < 10)
            return false;

        _lastPythonAttempt = DateTimeOffset.UtcNow;

        if (!IsPythonPortReachable())
        {
            _logger.LogDebug("Python service not listening at {Address}", _pythonAddress);
            return false;
        }

        try
        {
            if (_python == null)
            {
                _channel?.Dispose();
                _channel = GrpcChannel.ForAddress(_pythonAddress, new GrpcChannelOptions
                {
                    HttpHandler = new SocketsHttpHandler
                    {
                        EnableMultipleHttp2Connections = true,
                        PooledConnectionIdleTimeout = Timeout.InfiniteTimeSpan
                    }
                });
                _python = new SignalProcessorService.SignalProcessorServiceClient(_channel);
            }

            return true;
        }
        catch (Exception ex)
        {
            _pythonAvailable = false;
            _logger.LogWarning(ex, "Could not prepare Python client for {Address}", _pythonAddress);
            return false;
        }
    }

    private bool IsPythonPortReachable()
    {
        try
        {
            var uri = new Uri(_pythonAddress);
            var port = uri.Port > 0 ? uri.Port : 5051;
            using var tcp = new System.Net.Sockets.TcpClient();
            var connect = tcp.ConnectAsync(uri.Host, port);
            if (!connect.Wait(TimeSpan.FromSeconds(2)))
                return false;
            return tcp.Connected;
        }
        catch
        {
            return false;
        }
    }

    private async Task PersistAsync(ScanBatch batch, EnvironmentState state, bool fromPython, CancellationToken ct)
    {
        try
        {
            long ts = batch.Timestamp.ToUnixTimeMilliseconds();
            var features = state.Signals.Select(s => new WiFiFeatureRecord
            {
                BatchId = batch.BatchId.ToString(),
                Bssid = s.Bssid,
                Ssid = s.Ssid,
                TimestampMs = ts,
                RawRssi = s.RawRssi,
                SmoothedRssi = s.SmoothedRssi,
                Variance = s.Variance,
                StdDev = s.StdDev,
                ChangeRate = s.ChangeRate,
                Entropy = s.Entropy,
                CrossCorrelation = s.CrossCorrelation,
                DominantFrequency = s.DominantFrequency,
                SpectralEnergy = s.SpectralEnergy,
                ZScore = s.ZScore,
                IsAnomaly = s.IsAnomaly,
                MotionConfidence = s.MotionConfidence
            }).ToList();

            if (features.Count > 0)
                await _store.StoreFeaturesAsync(features, ct);

            if (state.ActiveEvents.Count > 0)
                await _store.StoreMotionEventsAsync(batch.BatchId.ToString(), ts, state.ActiveEvents, ct);

            if (state.Zones.Count > 0)
                await _store.StoreZoneStatesAsync(batch.BatchId.ToString(), ts, state.Zones, ct);

            await _store.StoreEnvironmentBatchAsync(new EnvironmentBatchRecord
            {
                BatchId = batch.BatchId.ToString(),
                TimestampMs = ts,
                MotionConfidence = state.MotionConfidence,
                OccupancyConfidence = state.OccupancyConfidence,
                Classification = (int)state.Classification,
                StabilityIndex = state.StabilityIndex,
                LstmMotionConfidence = state.LstmMotionConfidence,
                CnnActivityScore = state.CnnActivityScore,
                ActiveApCount = state.ActiveApCount > 0 ? state.ActiveApCount : state.Signals.Count,
                Source = fromPython ? "Python" : "Fallback"
            }, ct);

            _logger.LogDebug(
                "Persisted batch {BatchId}: Features={FeatureCount}, Events={EventCount}, Zones={ZoneCount}, Source={Source}",
                batch.BatchId, features.Count, state.ActiveEvents.Count, state.Zones.Count,
                fromPython ? "Python" : "Fallback");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to persist processing results for batch {BatchId}", batch.BatchId);
        }
    }

    private void MergeEvents(EnvironmentState state)
    {
        foreach (var evt in state.ActiveEvents)
        {
            _recentEvents.Add(evt);
        }
        while (_recentEvents.Count > 200)
            _recentEvents.RemoveAt(0);
    }

    private static MeasurementBatch ToMeasurementBatch(ScanBatch batch)
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
        return msg;
    }

    private static EnvironmentState MapFromPython(ProcessingResult r, ScanBatch batch)
    {
        var state = new EnvironmentState
        {
            Timestamp = r.Timestamp?.ToDateTimeOffset() ?? batch.Timestamp,
            MotionConfidence = r.MotionConfidence,
            OccupancyConfidence = r.OccupancyConfidence,
            Classification = (EnvironmentClassification)r.Classification,
            ActiveApCount = r.ActiveApCount,
            StabilityIndex = r.StabilityIndex,
            LstmMotionConfidence = r.LstmMotionConfidence,
            CnnActivityScore = r.CnnActivityScore
        };

        foreach (var s in r.Signals)
        {
            state.Signals.Add(new ProcessedSignal
            {
                Bssid = s.Bssid,
                Ssid = s.Ssid,
                RawRssi = s.RawRssi,
                SmoothedRssi = s.SmoothedRssi,
                Variance = s.Variance,
                StdDev = s.StdDev,
                ChangeRate = s.ChangeRate,
                DominantFrequency = s.DominantFrequency,
                SpectralEnergy = s.SpectralEnergy,
                ZScore = s.ZScore,
                IsAnomaly = s.IsAnomaly,
                Entropy = s.Entropy,
                CrossCorrelation = s.CrossCorrelation,
                MotionConfidence = Math.Min(1.0, s.Variance / 6.0),
                FftMagnitudes = s.FftMagnitudes.ToArray(),
                FftFrequencies = s.FftFrequencies.ToArray(),
                Timestamp = state.Timestamp
            });
        }

        foreach (var e in r.Events)
        {
            state.ActiveEvents.Add(new MotionEvent
            {
                Id = Guid.NewGuid(),
                Timestamp = e.Timestamp?.ToDateTimeOffset() ?? state.Timestamp,
                EventType = MapEventType(e.EventType),
                Confidence = e.Confidence,
                Description = e.Description,
                AffectedApCount = e.AffectedApCount,
                AverageVariance = e.AverageVariance,
                PeakRssiChange = e.PeakRssiChange,
                DurationMs = e.DurationMs,
                AffectedBssids = e.AffectedBssids.ToList()
            });
        }

        foreach (var z in r.Zones)
        {
            state.Zones.Add(new SpatialZone
            {
                ZoneId = z.ZoneId,
                Name = z.Name,
                X = z.X,
                Y = z.Y,
                Radius = z.Radius,
                OccupancyConfidence = z.OccupancyConfidence,
                MotionConfidence = z.MotionConfidence,
                Color = z.Color,
                LastActivity = state.Timestamp
            });
        }

        return state;
    }

    private static MotionEventType MapEventType(MotionEventTypeMsg t) => t switch
    {
        MotionEventTypeMsg.SubtleMovement => MotionEventType.SubtleMovement,
        MotionEventTypeMsg.Movement => MotionEventType.Movement,
        MotionEventTypeMsg.RoomEntry => MotionEventType.RoomEntry,
        MotionEventTypeMsg.RoomExit => MotionEventType.RoomExit,
        MotionEventTypeMsg.EnvironmentalChange => MotionEventType.EnvironmentalChange,
        MotionEventTypeMsg.Occupancy => MotionEventType.Occupancy,
        MotionEventTypeMsg.Anomaly => MotionEventType.Anomaly,
        _ => MotionEventType.None
    };

    public void Dispose()
    {
        _channel?.Dispose();
        _processLock.Dispose();
    }
}
