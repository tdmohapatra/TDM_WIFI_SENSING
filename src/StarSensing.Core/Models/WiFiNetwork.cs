namespace StarSensing.Core.Models;

/// <summary>
/// Represents a detected Wi-Fi network access point.
/// </summary>
public sealed class WiFiNetwork
{
    /// <summary>Unique identifier for this network record.</summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Service Set Identifier (network name).</summary>
    public string Ssid { get; set; } = string.Empty;

    /// <summary>Basic Service Set Identifier (MAC address of the AP).</summary>
    public string Bssid { get; set; } = string.Empty;

    /// <summary>Received Signal Strength Indicator in dBm (typically -90 to -20).</summary>
    public int RssiDbm { get; set; }

    /// <summary>Signal quality as a percentage (0-100).</summary>
    public int SignalQuality { get; set; }

    /// <summary>Frequency in kHz (e.g., 2412000 for Channel 1).</summary>
    public int FrequencyKHz { get; set; }

    /// <summary>Wi-Fi channel number.</summary>
    public int Channel { get; set; }

    /// <summary>Band (2.4 GHz or 5 GHz).</summary>
    public string Band { get; set; } = string.Empty;

    /// <summary>Timestamp when this measurement was captured.</summary>
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>The Wi-Fi interface ID that detected this network.</summary>
    public Guid InterfaceId { get; set; }

    /// <summary>
    /// Derives the channel number from frequency.
    /// </summary>
    public static int FrequencyToChannel(int frequencyKHz)
    {
        int freqMHz = frequencyKHz / 1000;
        if (freqMHz >= 2412 && freqMHz <= 2484)
        {
            if (freqMHz == 2484) return 14;
            return (freqMHz - 2412) / 5 + 1;
        }
        if (freqMHz >= 5170 && freqMHz <= 5825)
        {
            return (freqMHz - 5170) / 5 + 34;
        }
        return 0;
    }

    /// <summary>
    /// Determines the band string from frequency.
    /// </summary>
    public static string FrequencyToBand(int frequencyKHz)
    {
        int freqMHz = frequencyKHz / 1000;
        return freqMHz < 5000 ? "2.4 GHz" : "5 GHz";
    }
}
