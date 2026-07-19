namespace StarSensing.Core.Models;

/// <summary>
/// Represents a detected motion or environmental change event.
/// </summary>
public sealed class MotionEvent
{
    /// <summary>Unique event identifier.</summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>When the event was detected.</summary>
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Type of detected event.</summary>
    public MotionEventType EventType { get; set; }

    /// <summary>Confidence level (0.0 to 1.0).</summary>
    public double Confidence { get; set; }

    /// <summary>Human-readable description of the event.</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>Number of APs that contributed to this detection.</summary>
    public int AffectedApCount { get; set; }

    /// <summary>BSSIDs that showed correlated changes.</summary>
    public List<string> AffectedBssids { get; set; } = new();

    /// <summary>Average RSSI variance across affected APs during event.</summary>
    public double AverageVariance { get; set; }

    /// <summary>Peak RSSI change magnitude in dBm.</summary>
    public double PeakRssiChange { get; set; }

    /// <summary>Duration of the event in milliseconds.</summary>
    public int DurationMs { get; set; }
}

/// <summary>
/// Types of motion/environmental events the system can detect.
/// </summary>
public enum MotionEventType
{
    /// <summary>No significant activity.</summary>
    None = 0,

    /// <summary>Subtle signal variation suggesting nearby movement.</summary>
    SubtleMovement = 1,

    /// <summary>Clear human movement detected.</summary>
    Movement = 2,

    /// <summary>Person entering the detection zone.</summary>
    RoomEntry = 3,

    /// <summary>Person leaving the detection zone.</summary>
    RoomExit = 4,

    /// <summary>Large environmental disturbance (door, furniture).</summary>
    EnvironmentalChange = 5,

    /// <summary>Persistent presence detected (occupancy).</summary>
    Occupancy = 6,

    /// <summary>Signal anomaly that doesn't match known patterns.</summary>
    Anomaly = 7
}
