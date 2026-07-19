using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StarSensing.Dashboard.Services;

namespace StarSensing.Dashboard.ViewModels;

public partial class WifiNetworkItem : ObservableObject
{
    [ObservableProperty] private string _ssid = string.Empty;
    [ObservableProperty] private string _authentication = string.Empty;
    [ObservableProperty] private string _signal = string.Empty;
    [ObservableProperty] private int _signalPercent;
    [ObservableProperty] private string _channel = string.Empty;
    [ObservableProperty] private string _bssid = string.Empty;
    [ObservableProperty] private string _radioType = string.Empty;

    public bool IsSecured => !Authentication.Contains("Open", StringComparison.OrdinalIgnoreCase);
    public string SecurityText => IsSecured ? Authentication : "Open";
    public double SignalBarWidth => Math.Clamp(SignalPercent, 0, 100);
}

public partial class WifiProfileItem : ObservableObject
{
    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string _authentication = string.Empty;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PasswordDisplay))]
    private string _password = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PasswordDisplay))]
    private bool _passwordVisible;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PasswordDisplay))]
    private bool _passwordLoaded;

    public string PasswordDisplay => PasswordVisible ? (PasswordLoaded ? Password : "(not loaded)") : "••••••••";
}

public partial class NetworkOpsViewModel : ObservableObject
{
    public ObservableCollection<WifiNetworkItem> AvailableNetworks { get; } = new();
    public ObservableCollection<WifiProfileItem> SavedProfiles { get; } = new();

    [ObservableProperty] private WifiNetworkItem? _selectedNetwork;
    [ObservableProperty] private WifiProfileItem? _selectedProfile;
    [ObservableProperty] private string _password = string.Empty;
    [ObservableProperty] private string _statusText = "Idle";
    [ObservableProperty] private string _currentConnection = "Not connected";
    [ObservableProperty] private string _connectedBssid = string.Empty;
    [ObservableProperty] private string _commandOutput = "Run a command to see output here...";
    [ObservableProperty] private string _pingTarget = "8.8.8.8";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _showPasswordEntry = true;
    [ObservableProperty] private int _selectedTabIndex;

    public NetworkOpsViewModel()
    {
        _ = ScanAsync();
        _ = RefreshProfilesAsync();
        _ = RefreshCurrentConnectionAsync();
    }

    partial void OnSelectedProfileChanged(WifiProfileItem? value)
    {
        if (value != null && value.PasswordLoaded)
            Password = value.Password;
    }

    [RelayCommand]
    private async Task ScanAsync()
    {
        IsBusy = true;
        StatusText = "Scanning networks...";
        try
        {
            var parsed = await WifiOperationsService.ScanNetworksAsync();
            Application.Current.Dispatcher.Invoke(() =>
            {
                AvailableNetworks.Clear();
                foreach (var n in parsed.OrderByDescending(x => x.SignalPercent))
                {
                    AvailableNetworks.Add(new WifiNetworkItem
                    {
                        Ssid = n.Ssid,
                        Bssid = n.Bssid,
                        Authentication = n.Authentication,
                        Signal = n.Signal,
                        SignalPercent = n.SignalPercent,
                        Channel = n.Channel,
                        RadioType = n.RadioType
                    });
                }
            });
            StatusText = $"Found {parsed.Count} BSS entries";
        }
        catch (Exception ex)
        {
            StatusText = $"Scan failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task RefreshProfilesAsync()
    {
        IsBusy = true;
        try
        {
            var names = await WifiOperationsService.ListProfileNamesAsync();
            Application.Current.Dispatcher.Invoke(() =>
            {
                SavedProfiles.Clear();
                foreach (var name in names.OrderBy(n => n))
                    SavedProfiles.Add(new WifiProfileItem { Name = name });
            });
            StatusText = $"Loaded {names.Count} saved profiles";
        }
        catch (Exception ex)
        {
            StatusText = $"Profile list failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ShowProfilePasswordAsync()
    {
        if (SelectedProfile == null)
        {
            StatusText = "Select a saved profile first.";
            return;
        }

        IsBusy = true;
        StatusText = $"Loading password for {SelectedProfile.Name}...";
        try
        {
            var profile = await WifiOperationsService.LoadProfileAsync(SelectedProfile.Name);
            if (profile == null)
            {
                StatusText = "Could not read profile (run Dashboard as same user who saved it).";
                return;
            }

            SelectedProfile.Authentication = profile.Authentication;
            SelectedProfile.Password = profile.Password;
            SelectedProfile.PasswordLoaded = profile.PasswordLoaded;
            SelectedProfile.PasswordVisible = true;
            Password = profile.Password;
            StatusText = profile.PasswordLoaded
                ? $"Password loaded for {SelectedProfile.Name}"
                : $"Profile {SelectedProfile.Name} has no stored key (enterprise/open).";
        }
        catch (Exception ex)
        {
            StatusText = $"Password read failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void TogglePasswordVisibility()
    {
        if (SelectedProfile != null)
            SelectedProfile.PasswordVisible = !SelectedProfile.PasswordVisible;
        ShowPasswordEntry = !ShowPasswordEntry;
    }

    [RelayCommand]
    private async Task ConnectAsync()
    {
        if (SelectedNetwork == null)
        {
            StatusText = "Select a scanned network first.";
            return;
        }

        IsBusy = true;
        var ssid = SelectedNetwork.Ssid;
        StatusText = $"Connecting to {ssid}...";
        try
        {
            string pwd = Password;
            if (string.IsNullOrEmpty(pwd) && SelectedProfile?.PasswordLoaded == true)
                pwd = SelectedProfile.Password;

            CommandOutput = await WifiOperationsService.ConnectAsync(ssid, SelectedNetwork.IsSecured, pwd);
            await Task.Delay(1500);
            await RefreshCurrentConnectionAsync();
            StatusText = $"Connect requested for {ssid}";
        }
        catch (Exception ex)
        {
            StatusText = $"Connect failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ConnectFromProfileAsync()
    {
        if (SelectedProfile == null)
        {
            StatusText = "Select a saved profile.";
            return;
        }

        IsBusy = true;
        try
        {
            if (!SelectedProfile.PasswordLoaded)
                await ShowProfilePasswordAsync();

            CommandOutput = await WifiOperationsService.RunCommandAsync("netsh",
                $"wlan connect name=\"{SelectedProfile.Name.Replace("\"", "\\\"")}\"");
            await Task.Delay(1500);
            await RefreshCurrentConnectionAsync();
            StatusText = $"Connected using profile {SelectedProfile.Name}";
        }
        catch (Exception ex)
        {
            StatusText = $"Connect failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task DisconnectAsync()
    {
        IsBusy = true;
        try
        {
            CommandOutput = await WifiOperationsService.DisconnectAsync();
            await Task.Delay(800);
            await RefreshCurrentConnectionAsync();
            StatusText = "Disconnected";
        }
        catch (Exception ex)
        {
            StatusText = $"Disconnect failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task UseSelectedNetworkPasswordFromProfileAsync()
    {
        if (SelectedNetwork == null) return;
        var match = SavedProfiles.FirstOrDefault(p =>
            string.Equals(p.Name, SelectedNetwork.Ssid, StringComparison.OrdinalIgnoreCase));
        if (match == null)
        {
            StatusText = "No saved profile matches this SSID.";
            return;
        }

        SelectedProfile = match;
        await ShowProfilePasswordAsync();
    }

    private async Task RefreshCurrentConnectionAsync()
    {
        try
        {
            var info = await WifiOperationsService.GetCurrentConnectionAsync();
            Application.Current.Dispatcher.Invoke(() =>
            {
                ConnectedBssid = info.Bssid;
                CurrentConnection = info.IsConnected && info.Ssid.Length > 0
                    ? $"Connected: {info.Ssid} ({info.Signal})"
                    : "Not connected";
            });
        }
        catch
        {
            CurrentConnection = "Unknown";
        }
    }

    [RelayCommand] private Task ShowInterfaces() => RunIntoOutput("netsh", "wlan show interfaces");
    [RelayCommand] private Task ShowNetworks() => RunIntoOutput("netsh", "wlan show networks mode=bssid");
    [RelayCommand] private Task ShowProfiles() => RunIntoOutput("netsh", "wlan show profiles");
    [RelayCommand] private Task ShowDrivers() => RunIntoOutput("netsh", "wlan show drivers");
    [RelayCommand] private Task IpConfig() => RunIntoOutput("ipconfig", "/all");
    [RelayCommand] private Task FlushDns() => RunIntoOutput("ipconfig", "/flushdns");
    [RelayCommand] private Task Ping() => RunIntoOutput("ping", $"-n 4 {PingTarget}");

    private async Task RunIntoOutput(string exe, string args)
    {
        IsBusy = true;
        StatusText = $"Running: {exe} {args}";
        try
        {
            CommandOutput = await WifiOperationsService.RunCommandAsync(exe, args);
            StatusText = "Done";
        }
        catch (Exception ex)
        {
            CommandOutput = ex.Message;
            StatusText = "Command failed";
        }
        finally
        {
            IsBusy = false;
        }
    }
}
