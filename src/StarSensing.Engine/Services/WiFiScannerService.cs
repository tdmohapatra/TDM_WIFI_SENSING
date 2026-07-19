using ManagedNativeWifi;
using StarSensing.Core.Interfaces;
using StarSensing.Core.Models;

namespace StarSensing.Engine.Services;

public class WiFiScannerService : IWiFiScanner
{
    private readonly ILogger<WiFiScannerService> _logger;
    private readonly List<Guid> _interfaces = new();
    private DateTimeOffset _lastTriggerScan = DateTimeOffset.MinValue;
    private static readonly TimeSpan TriggerScanInterval = TimeSpan.FromSeconds(2);

    public bool IsAvailable => _interfaces.Count > 0;

    public WiFiScannerService(ILogger<WiFiScannerService> logger)
    {
        _logger = logger;
        InitializeInterfaces();
    }

    private void InitializeInterfaces()
    {
        try
        {
            var ifaces = NativeWifi.EnumerateInterfaces();
            foreach (var iface in ifaces)
            {
                _interfaces.Add(iface.Id);
                _logger.LogInformation("Found Wi-Fi Interface: {Id} - {Description}", iface.Id, iface.Description);
            }
            
            if (_interfaces.Count == 0)
            {
                _logger.LogWarning("No Wi-Fi interfaces found. Scanning will not work.");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to enumerate Wi-Fi interfaces. Ensure Location services are enabled in Windows.");
        }
    }

    public IReadOnlyList<Guid> GetInterfaceIds() => _interfaces;

    public async Task<ScanBatch> ScanAsync(CancellationToken cancellationToken = default)
    {
        if (!IsAvailable) return new ScanBatch { ScanDurationMs = 0 };

        var batch = new ScanBatch { Timestamp = DateTimeOffset.UtcNow };
        var start = DateTimeOffset.UtcNow;

        try
        {
            // Trigger a fresh passive scan periodically; enumerate every tick from driver cache.
            if (DateTimeOffset.UtcNow - _lastTriggerScan >= TriggerScanInterval)
            {
                try
                {
                    await NativeWifi.ScanNetworksAsync(TimeSpan.FromSeconds(2), cancellationToken);
                    _lastTriggerScan = DateTimeOffset.UtcNow;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Wi-Fi scan trigger failed; using cached BSS list.");
                }
            }

            foreach (var ifaceId in _interfaces)
            {
                IReadOnlyList<BssNetworkInfo> bssNetworks;
                try
                {
                    var (_, networks) = NativeWifi.EnumerateBssNetworks(ifaceId);
                    bssNetworks = networks?.ToList() ?? [];
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to enumerate BSS networks on interface {InterfaceId}.", ifaceId);
                    continue;
                }

                foreach (var bss in bssNetworks)
                {
                    if (bss?.Bssid is null) continue;

                    var measurement = new SignalMeasurement
                    {
                        Bssid = bss.Bssid.ToString(),
                        Ssid = bss.Ssid?.ToString() ?? "Hidden",
                        RssiDbm = bss.Rssi,
                        SignalQuality = bss.LinkQuality,
                        FrequencyKHz = (int)bss.Frequency,
                        Channel = WiFiNetwork.FrequencyToChannel((int)bss.Frequency),
                        Timestamp = DateTimeOffset.UtcNow
                    };
                    batch.Measurements.Add(measurement);
                }
            }

            if (batch.NetworkCount == 0)
                _logger.LogDebug("Wi-Fi enumerate returned 0 networks (driver may still be scanning).");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during Wi-Fi scan.");
        }

        batch.ScanDurationMs = (int)(DateTimeOffset.UtcNow - start).TotalMilliseconds;
        return batch;
    }
}
