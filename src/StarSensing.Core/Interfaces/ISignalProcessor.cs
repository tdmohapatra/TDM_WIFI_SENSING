using StarSensing.Core.Models;

namespace StarSensing.Core.Interfaces;

/// <summary>
/// Calls the Python signal processor and maps results to domain models.
/// </summary>
public interface ISignalProcessor
{
    /// <summary>Process a scan batch via Python (with local fallback).</summary>
    Task<EnvironmentState> ProcessAsync(ScanBatch batch, CancellationToken ct = default);

    /// <summary>Latest cached environment state for a batch id, if available.</summary>
    EnvironmentState? GetCachedState(Guid batchId);
}
