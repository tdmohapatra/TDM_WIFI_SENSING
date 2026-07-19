namespace StarSensing.Dashboard.Services;

/// <summary>Reads device compass heading when available (Windows 10+ sensor API).</summary>
public sealed class CompassService : IDisposable
{
    private object? _compass;
    private bool _available;
    private double _lastHeading;
    private DateTimeOffset _lastRead = DateTimeOffset.MinValue;

    public bool IsAvailable => _available;

    public string StatusText { get; private set; } = "Compass not initialized";

    public void Initialize()
    {
        try
        {
            var compassType = Type.GetType("Windows.Devices.Sensors.Compass, Windows, ContentType=WindowsRuntime");
            if (compassType == null)
            {
                StatusText = "Compass API unavailable on this system";
                return;
            }

            var getDefault = compassType.GetMethod("GetDefault", Type.EmptyTypes);
            _compass = getDefault?.Invoke(null, null);
            if (_compass == null)
            {
                StatusText = "No compass sensor on this device";
                return;
            }

            _available = true;
            StatusText = "Compass ready — face the router and capture bearing";
        }
        catch (Exception ex)
        {
            StatusText = $"Compass error: {ex.Message}";
            _available = false;
        }
    }

    /// <summary>Heading in degrees (0=N, 90=E, clockwise). Returns null if unavailable.</summary>
    public double? TryGetHeading()
    {
        if (!_available || _compass == null) return null;

        try
        {
            var readingProp = _compass.GetType().GetProperty("GetCurrentReading");
            var reading = readingProp?.GetMethod?.Invoke(_compass, null);
            if (reading == null) return _lastHeading > 0 ? _lastHeading : null;

            var headingProp = reading.GetType().GetProperty("HeadingMagneticNorth")
                ?? reading.GetType().GetProperty("TrueHeading");
            if (headingProp?.GetValue(reading) is not double heading)
                return _lastHeading > 0 ? _lastHeading : null;

            _lastHeading = BearingStoreService.NormalizeDeg(heading);
            _lastRead = DateTimeOffset.UtcNow;
            return _lastHeading;
        }
        catch
        {
            return _lastHeading > 0 && (DateTimeOffset.UtcNow - _lastRead).TotalSeconds < 5
                ? _lastHeading
                : null;
        }
    }

    public void Dispose() => _compass = null;
}
