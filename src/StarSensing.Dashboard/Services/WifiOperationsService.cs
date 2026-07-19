using System.Diagnostics;
using System.IO;
using System.Text;

namespace StarSensing.Dashboard.Services;

public sealed class WifiScanEntry
{
    public string Ssid { get; set; } = string.Empty;
    public string Bssid { get; set; } = string.Empty;
    public string Authentication { get; set; } = string.Empty;
    public string Signal { get; set; } = string.Empty;
    public int SignalPercent { get; set; }
    public string Channel { get; set; } = string.Empty;
    public string RadioType { get; set; } = string.Empty;

    public bool IsSecured => !Authentication.Contains("Open", StringComparison.OrdinalIgnoreCase);
    public string SecurityText => IsSecured ? Authentication : "Open";
}

public sealed class WifiProfileEntry
{
    public string Name { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public bool PasswordLoaded { get; set; }
    public string Authentication { get; set; } = string.Empty;
    public string ConnectionMode { get; set; } = string.Empty;
}

public sealed class WifiConnectionInfo
{
    public bool IsConnected { get; set; }
    public string Ssid { get; set; } = string.Empty;
    public string Bssid { get; set; } = string.Empty;
    public string Signal { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
}

/// <summary>Windows netsh wrapper for scan, connect, profiles, and password reveal.</summary>
public static class WifiOperationsService
{
    public static async Task<IReadOnlyList<WifiScanEntry>> ScanNetworksAsync()
    {
        var output = await RunAsync("netsh", "wlan show networks mode=bssid");
        return ParseScanOutput(output);
    }

    public static async Task<IReadOnlyList<string>> ListProfileNamesAsync()
    {
        var output = await RunAsync("netsh", "wlan show profiles");
        var names = new List<string>();
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            if (!line.Contains("Profile", StringComparison.OrdinalIgnoreCase) || !line.Contains(':'))
                continue;

            int idx = line.IndexOf(':');
            if (idx < 0) continue;
            string name = line[(idx + 1)..].Trim();
            if (name.Length > 0 && !name.StartsWith("----", StringComparison.Ordinal))
                names.Add(name);
        }
        return names;
    }

    public static async Task<WifiProfileEntry?> LoadProfileAsync(string profileName)
    {
        var escaped = profileName.Replace("\"", "\\\"");
        var output = await RunAsync("netsh", $"wlan show profile name=\"{escaped}\" key=clear");
        if (string.IsNullOrWhiteSpace(output)) return null;

        var entry = new WifiProfileEntry { Name = profileName };
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("Authentication", StringComparison.OrdinalIgnoreCase))
                entry.Authentication = ValueAfterColon(line);
            else if (line.StartsWith("Connection mode", StringComparison.OrdinalIgnoreCase))
                entry.ConnectionMode = ValueAfterColon(line);
            else if (line.StartsWith("Key Content", StringComparison.OrdinalIgnoreCase))
            {
                entry.Password = ValueAfterColon(line);
                entry.PasswordLoaded = entry.Password.Length > 0;
            }
        }
        return entry;
    }

    public static async Task<WifiConnectionInfo> GetCurrentConnectionAsync()
    {
        var output = await RunAsync("netsh", "wlan show interfaces");
        var info = new WifiConnectionInfo();
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("State", StringComparison.OrdinalIgnoreCase))
            {
                info.State = ValueAfterColon(line);
                info.IsConnected = info.State.Contains("connected", StringComparison.OrdinalIgnoreCase);
            }
            else if (line.StartsWith("SSID", StringComparison.OrdinalIgnoreCase) &&
                     !line.StartsWith("BSSID", StringComparison.OrdinalIgnoreCase))
                info.Ssid = ValueAfterColon(line);
            else if (line.StartsWith("BSSID", StringComparison.OrdinalIgnoreCase))
                info.Bssid = ValueAfterColon(line);
            else if (line.StartsWith("Signal", StringComparison.OrdinalIgnoreCase))
                info.Signal = ValueAfterColon(line);
        }
        return info;
    }

    public static async Task<string> ConnectAsync(string ssid, bool secured, string password)
    {
        string xml = BuildProfileXml(ssid, secured, password);
        string path = Path.Combine(Path.GetTempPath(), $"ss_{Sanitize(ssid)}.xml");
        await File.WriteAllTextAsync(path, xml, new UTF8Encoding(false));
        try
        {
            var add = await RunAsync("netsh", $"wlan add profile filename=\"{path}\" user=current");
            var connect = await RunAsync("netsh", $"wlan connect name=\"{ssid.Replace("\"", "\\\"")}\" ssid=\"{ssid.Replace("\"", "\\\"")}\"");
            return add + "\r\n" + connect;
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    public static Task<string> DisconnectAsync() => RunAsync("netsh", "wlan disconnect");

    public static Task<string> RunCommandAsync(string exe, string args) => RunAsync(exe, args);

    private static List<WifiScanEntry> ParseScanOutput(string output)
    {
        var list = new List<WifiScanEntry>();
        string currentSsid = "";
        string currentAuth = "";

        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();

            if (line.StartsWith("SSID ", StringComparison.OrdinalIgnoreCase) &&
                !line.StartsWith("BSSID", StringComparison.OrdinalIgnoreCase) &&
                line.Contains(':'))
            {
                currentSsid = ValueAfterColon(line);
                if (string.IsNullOrWhiteSpace(currentSsid)) currentSsid = "(hidden)";
                currentAuth = "";
            }
            else if (line.StartsWith("Authentication", StringComparison.OrdinalIgnoreCase))
                currentAuth = ValueAfterColon(line);
            else if (line.StartsWith("BSSID", StringComparison.OrdinalIgnoreCase) && currentSsid.Length > 0)
            {
                var entry = new WifiScanEntry
                {
                    Ssid = currentSsid,
                    Bssid = ValueAfterColon(line),
                    Authentication = currentAuth
                };
                list.Add(entry);
            }
            else if (list.Count > 0)
            {
                var last = list[^1];
                if (line.StartsWith("Signal", StringComparison.OrdinalIgnoreCase) && last.Signal.Length == 0)
                {
                    last.Signal = ValueAfterColon(line);
                    last.SignalPercent = ParseSignalPercent(last.Signal);
                }
                else if (line.StartsWith("Channel", StringComparison.OrdinalIgnoreCase) && last.Channel.Length == 0)
                    last.Channel = ValueAfterColon(line);
                else if (line.StartsWith("Radio type", StringComparison.OrdinalIgnoreCase) && last.RadioType.Length == 0)
                    last.RadioType = ValueAfterColon(line);
            }
        }

        return list;
    }

    private static int ParseSignalPercent(string signal)
    {
        var digits = new string(signal.Where(char.IsDigit).ToArray());
        return int.TryParse(digits, out var v) ? v : 0;
    }

    public static async Task<string> RunAsync(string exe, string args)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8
        };

        using var process = new Process { StartInfo = psi };
        process.Start();
        string stdout = await process.StandardOutput.ReadToEndAsync();
        string stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return string.IsNullOrWhiteSpace(stderr) ? stdout : stdout + "\r\n" + stderr;
    }

    private static string ValueAfterColon(string line)
    {
        int idx = line.IndexOf(':');
        return idx >= 0 ? line[(idx + 1)..].Trim() : string.Empty;
    }

    private static string Sanitize(string s)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            s = s.Replace(c, '_');
        return s;
    }

    private static string BuildProfileXml(string ssid, bool secured, string password)
    {
        string escaped = System.Security.SecurityElement.Escape(ssid) ?? ssid;
        if (!secured)
        {
            return $@"<?xml version=""1.0""?>
<WLANProfile xmlns=""http://www.microsoft.com/networking/WLAN/profile/v1"">
  <name>{escaped}</name>
  <SSIDConfig><SSID><name>{escaped}</name></SSID></SSIDConfig>
  <connectionType>ESS</connectionType>
  <connectionMode>auto</connectionMode>
  <MSM><security>
    <authEncryption><authentication>open</authentication><encryption>none</encryption><useOneX>false</useOneX></authEncryption>
  </security></MSM>
</WLANProfile>";
        }

        string key = System.Security.SecurityElement.Escape(password) ?? password;
        return $@"<?xml version=""1.0""?>
<WLANProfile xmlns=""http://www.microsoft.com/networking/WLAN/profile/v1"">
  <name>{escaped}</name>
  <SSIDConfig><SSID><name>{escaped}</name></SSID></SSIDConfig>
  <connectionType>ESS</connectionType>
  <connectionMode>auto</connectionMode>
  <MSM><security>
    <authEncryption><authentication>WPA2PSK</authentication><encryption>AES</encryption><useOneX>false</useOneX></authEncryption>
    <sharedKey><keyType>passPhrase</keyType><protected>false</protected><keyMaterial>{key}</keyMaterial></sharedKey>
  </security></MSM>
</WLANProfile>";
    }
}
