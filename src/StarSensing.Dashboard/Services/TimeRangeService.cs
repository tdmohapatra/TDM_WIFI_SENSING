using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace StarSensing.Dashboard.Services;

/// <summary>Shared live vs historical time window for all dashboard tabs.</summary>
public partial class TimeRangeService : ObservableObject
{
    [ObservableProperty] private bool _isLiveMode = true;
    [ObservableProperty] private int _rangeMinutes = 30;

    public DateTimeOffset FromUtc => DateTimeOffset.UtcNow.AddMinutes(-RangeMinutes);
    public DateTimeOffset ToUtc => DateTimeOffset.UtcNow;

    public string ModeLabel => IsLiveMode ? "Live" : $"History ({RangeMinutes}m)";

    partial void OnIsLiveModeChanged(bool value) => OnPropertyChanged(nameof(ModeLabel));
    partial void OnRangeMinutesChanged(int value) => OnPropertyChanged(nameof(ModeLabel));

    [RelayCommand]
    private void SetLive()
    {
        IsLiveMode = true;
    }

    [RelayCommand]
    private void SetHistorical()
    {
        IsLiveMode = false;
    }

    [RelayCommand]
    private void SetRange5m() => RangeMinutes = 5;

    [RelayCommand]
    private void SetRange15m() => RangeMinutes = 15;

    [RelayCommand]
    private void SetRange30m() => RangeMinutes = 30;

    [RelayCommand]
    private void SetRange1h() => RangeMinutes = 60;

    [RelayCommand]
    private void SetRange24h() => RangeMinutes = 1440;
}
