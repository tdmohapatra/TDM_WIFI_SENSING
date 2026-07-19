using StarSensing.Core.Interfaces;
using StarSensing.Engine.Services;

namespace StarSensing.Engine.Workers;

public class ScanWorker : BackgroundService
{
    private readonly ILogger<ScanWorker> _logger;
    private readonly IWiFiScanner _scanner;
    private readonly ISignalStore _store;
    private readonly SignalAggregator _aggregator;
    private readonly ISignalProcessor _processor;
    private readonly int _scanIntervalMs;
    private readonly int _trainingRetentionHours;
    private readonly int _rawRetentionHours;
    private readonly int _replayRetentionHours;
    private readonly int _accessPointMaxAgeDays;

    public ScanWorker(
        ILogger<ScanWorker> logger,
        IWiFiScanner scanner,
        ISignalStore store,
        SignalAggregator aggregator,
        ISignalProcessor processor,
        IConfiguration config)
    {
        _logger = logger;
        _scanner = scanner;
        _store = store;
        _aggregator = aggregator;
        _processor = processor;
        _scanIntervalMs = config.GetValue<int>("SensingConfig:ScanIntervalMs", 50);
        _trainingRetentionHours = config.GetValue<int>("SensingConfig:DataRetentionHours", 168);
        _rawRetentionHours = config.GetValue<int>("SensingConfig:RawDataRetentionHours", 24);
        _replayRetentionHours = config.GetValue<int>("SensingConfig:ReplayDataRetentionHours", 48);
        _accessPointMaxAgeDays = config.GetValue<int>("SensingConfig:AccessPointMaxAgeDays", 30);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("ScanWorker starting with {Interval}ms interval.", _scanIntervalMs);

        await _store.InitializeAsync(stoppingToken);

        DateTimeOffset lastPurge = DateTimeOffset.UtcNow;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var batch = await _scanner.ScanAsync(stoppingToken);
                if (batch.NetworkCount > 0)
                {
                    // 1) Raw scan → SQL (WiFi_Scan_Raw view / Measurements)
                    await _store.StoreBatchAsync(batch, stoppingToken);
                    // 2) Python ML features → SQL (WiFi_Features, Motion_Events, Zone_State)
                    await _processor.ProcessAsync(batch, stoppingToken);
                    // 3) Publish to dashboard streams (state already cached)
                    await _aggregator.PublishBatchAsync(batch, stoppingToken);
                }

                if (DateTimeOffset.UtcNow - lastPurge > TimeSpan.FromHours(1))
                {
                    var purge = await _store.PurgeNonTrainingDataAsync(
                        _trainingRetentionHours,
                        _rawRetentionHours,
                        _replayRetentionHours,
                        _accessPointMaxAgeDays,
                        dryRun: false,
                        stoppingToken);
                    _logger.LogInformation(
                        "Purge complete: removed {Total} rows (Measurements={Meas}, Features={Feat}, Zones={Zones}, Events={Events}, EnvBatches={Env}).",
                        purge.TotalDeleted,
                        purge.MeasurementsDeleted,
                        purge.WiFiFeaturesDeleted,
                        purge.ZoneStateDeleted,
                        purge.MotionEventsDeleted,
                        purge.EnvironmentBatchesDeleted);
                    lastPurge = DateTimeOffset.UtcNow;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in scan loop.");
            }

            await Task.Delay(_scanIntervalMs, stoppingToken);
        }
    }
}
