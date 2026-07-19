using StarSensing.Core.Models;

namespace StarSensing.Core.Interfaces;

/// <summary>
/// Interface for motion/environmental change detection.
/// </summary>
public interface IMotionDetector
{
    /// <summary>
    /// Analyzes a batch of measurements and returns the current environment state.
    /// </summary>
    Task<EnvironmentState> AnalyzeAsync(ScanBatch batch, CancellationToken ct = default);

    /// <summary>
    /// Gets the current motion confidence level.
    /// </summary>
    double CurrentMotionConfidence { get; }

    /// <summary>
    /// Gets recent motion events.
    /// </summary>
    IReadOnlyList<MotionEvent> RecentEvents { get; }

    /// <summary>
    /// Resets the baseline for motion detection (recalibrate).
    /// </summary>
    Task ResetBaselineAsync(CancellationToken ct = default);
}
