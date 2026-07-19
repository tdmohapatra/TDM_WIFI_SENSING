using StarSensing.Core.Interfaces;
using StarSensing.Core.Models;

namespace StarSensing.Engine.Services;

public class MotionDetectorService : IMotionDetector
{
    private readonly ILogger<MotionDetectorService> _logger;
    private readonly SemaphoreSlim _analysisLock = new(1, 1);
    private readonly double _subtleMovementVariance;
    private readonly double _movementVariance;
    private readonly double _highActivityVariance;
    private double _motionConfidence;
    private readonly List<MotionEvent> _recentEvents = new();
    
    // Basic variance tracking per BSSID
    private readonly Dictionary<string, Queue<int>> _rssiWindows = new();
    private const int WindowSize = 10;

    public double CurrentMotionConfidence => _motionConfidence;
    public IReadOnlyList<MotionEvent> RecentEvents => _recentEvents;

    public MotionDetectorService(ILogger<MotionDetectorService> logger, IConfiguration config)
    {
        _logger = logger;
        _subtleMovementVariance = config.GetValue<double>("SensingConfig:MotionThresholds:SubtleMovementVariance", 1.5);
        _movementVariance = config.GetValue<double>("SensingConfig:MotionThresholds:MovementVariance", 3.0);
        _highActivityVariance = config.GetValue<double>("SensingConfig:MotionThresholds:HighActivityVariance", 6.0);
    }

    public async Task<EnvironmentState> AnalyzeAsync(ScanBatch batch, CancellationToken ct = default)
    {
        await _analysisLock.WaitAsync(ct);
        try
        {
            var state = new EnvironmentState { Timestamp = DateTimeOffset.UtcNow };
            int activeAps = 0;
            double totalVariance = 0;

            foreach (var m in batch.Measurements)
            {
                if (!_rssiWindows.TryGetValue(m.Bssid, out var window))
                {
                    window = new Queue<int>();
                    _rssiWindows[m.Bssid] = window;
                }

                window.Enqueue(m.RssiDbm);
                if (window.Count > WindowSize) window.Dequeue();

                if (window.Count == WindowSize)
                {
                    // Snapshot the queue before LINQ operations so analysis is stable.
                    int[] samples = window.ToArray();
                    double avg = samples.Average();
                    double variance = samples.Select(val => Math.Pow(val - avg, 2)).Average();
                    
                    var processed = new ProcessedSignal
                    {
                        Bssid = m.Bssid,
                        Ssid = m.Ssid,
                        RawRssi = m.RssiDbm,
                        SmoothedRssi = avg,
                        Variance = variance,
                        StdDev = Math.Sqrt(variance),
                        Timestamp = DateTimeOffset.UtcNow
                    };

                    state.Signals.Add(processed);
                    totalVariance += variance;
                    activeAps++;
                }
            }

            if (activeAps > 0)
            {
                double avgVariance = totalVariance / activeAps;
                // Simple threshold logic
                if (avgVariance > _highActivityVariance) _motionConfidence = 1.0;
                else if (avgVariance > _movementVariance) _motionConfidence = 0.7;
                else if (avgVariance > _subtleMovementVariance) _motionConfidence = 0.3;
                else _motionConfidence = 0.0;
                
                state.MotionConfidence = _motionConfidence;
                state.ActiveApCount = activeAps;
                
                if (avgVariance > _subtleMovementVariance)
                {
                    var eventType = avgVariance > _highActivityVariance
                        ? MotionEventType.EnvironmentalChange
                        : avgVariance > _movementVariance
                            ? MotionEventType.Movement
                            : MotionEventType.SubtleMovement;

                    var affectedBssids = state.Signals
                        .OrderByDescending(s => s.Variance)
                        .Take(5)
                        .Select(s => s.Bssid)
                        .ToList();

                    var evt = new MotionEvent
                    {
                        EventType = eventType,
                        Confidence = _motionConfidence,
                        Description = eventType == MotionEventType.SubtleMovement
                            ? $"Minute movement detected across {activeAps} APs"
                            : $"Movement detected across {activeAps} APs",
                        AffectedApCount = activeAps,
                        AffectedBssids = affectedBssids,
                        AverageVariance = avgVariance,
                        PeakRssiChange = state.Signals.Count == 0 ? 0 : state.Signals.Max(s => s.StdDev)
                    };
                    _recentEvents.Add(evt);
                    if (_recentEvents.Count > 50) _recentEvents.RemoveAt(0);
                    state.ActiveEvents.Add(evt);

                    _logger.LogInformation(
                        "Motion event detected. Confidence={Confidence:P0}, ActiveAps={ActiveAps}, AvgVariance={AverageVariance:F2}, PeakStdDev={PeakStdDev:F2}",
                        _motionConfidence,
                        activeAps,
                        avgVariance,
                        evt.PeakRssiChange);
                }
            }

            _logger.LogDebug(
                "Analyzed scan batch {BatchId}: Measurements={MeasurementCount}, ActiveAps={ActiveAps}, MotionConfidence={MotionConfidence:P0}",
                batch.BatchId,
                batch.Measurements.Count,
                state.ActiveApCount,
                state.MotionConfidence);

            return state;
        }
        finally
        {
            _analysisLock.Release();
        }
    }

    public async Task ResetBaselineAsync(CancellationToken ct = default)
    {
        await _analysisLock.WaitAsync(ct);
        try
        {
            _rssiWindows.Clear();
            _recentEvents.Clear();
            _motionConfidence = 0;
            _logger.LogInformation("Motion detection baseline reset.");
        }
        finally
        {
            _analysisLock.Release();
        }
    }
}
