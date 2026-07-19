using StarSensing.Core.Models;

namespace StarSensing.Core.Interfaces;

/// <summary>
/// Interface for Wi-Fi network scanning operations.
/// </summary>
public interface IWiFiScanner
{
    /// <summary>
    /// Triggers a network scan and returns discovered networks.
    /// </summary>
    Task<ScanBatch> ScanAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the available Wi-Fi interface IDs.
    /// </summary>
    IReadOnlyList<Guid> GetInterfaceIds();

    /// <summary>
    /// Gets whether the scanner is operational.
    /// </summary>
    bool IsAvailable { get; }
}
