using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Windows;
using System.Windows.Threading;

namespace StarSensing.Dashboard.Services;

/// <summary>Loads SQL timeline for a time range and drives play/pause/scrub across tabs.</summary>
public partial class HistoricalTimelineService : ObservableObject
{
    private readonly SensingDataService _data;
    private readonly TimeRangeService _timeRange;
    private readonly DispatcherTimer _playTimer;
    private List<ReplayFrame> _frames = new();
    private int _index;

    public event Action<ReplayFrame>? FrameChanged;

    [ObservableProperty] private bool _isPlaying;
    [ObservableProperty] private bool _isHistoricalActive;
    [ObservableProperty] private double _scrubPosition;
    [ObservableProperty] private string _statusText = "Live mode";
    [ObservableProperty] private string _timeText = "--";
    [ObservableProperty] private int _frameCount;

    public IReadOnlyList<ReplayFrame> Frames => _frames;

    public HistoricalTimelineService(SensingDataService data, TimeRangeService timeRange)
    {
        _data = data;
        _timeRange = timeRange;
        _playTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _playTimer.Tick += (_, _) => Advance();
        _timeRange.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(TimeRangeService.IsLiveMode) or nameof(TimeRangeService.RangeMinutes))
                _ = ReloadAsync();
        };
    }

    public async Task ReloadAsync()
    {
        if (_timeRange.IsLiveMode)
        {
            StopInternal();
            IsHistoricalActive = false;
            StatusText = "Live mode — streaming real-time data.";
            return;
        }

        StatusText = "Loading SQL history...";
        long fromMs = _timeRange.FromUtc.ToUnixTimeMilliseconds();
        long toMs = _timeRange.ToUtc.ToUnixTimeMilliseconds();
        var timeline = await _data.GetReplayTimelineAsync(fromMs, toMs, 800);
        Application.Current.Dispatcher.Invoke(() =>
        {
            _frames = timeline.ToList();
            FrameCount = _frames.Count;
            _index = 0;
            IsHistoricalActive = _frames.Count > 0;
            ScrubPosition = 0;
            StatusText = _frames.Count > 0
                ? $"Loaded {_frames.Count} frames ({_timeRange.RangeMinutes}m window)."
                : "No history in range — run Engine to collect data.";
            if (_frames.Count > 0)
                ApplyFrame(_frames[0]);
        });
    }

    [RelayCommand]
    private async Task Play()
    {
        if (_timeRange.IsLiveMode)
            await ReloadAsync();
        if (_frames.Count == 0) return;
        IsPlaying = true;
        _playTimer.Start();
        StatusText = $"Playing {_frames.Count} frames...";
    }

    [RelayCommand]
    private void Pause()
    {
        IsPlaying = false;
        _playTimer.Stop();
    }

    [RelayCommand]
    private void Stop()
    {
        StopInternal();
        if (!_timeRange.IsLiveMode && _frames.Count > 0)
            StatusText = $"Stopped — {_frames.Count} frames loaded.";
    }

    partial void OnScrubPositionChanged(double value)
    {
        if (!IsHistoricalActive || _frames.Count == 0 || IsPlaying) return;
        int idx = (int)Math.Round(value / 100.0 * (_frames.Count - 1));
        idx = Math.Clamp(idx, 0, _frames.Count - 1);
        if (idx != _index)
        {
            _index = idx;
            ApplyFrame(_frames[idx]);
        }
    }

    private void Advance()
    {
        if (_frames.Count == 0) return;
        _index++;
        if (_index >= _frames.Count)
        {
            _index = 0;
            IsPlaying = false;
            _playTimer.Stop();
            StatusText = "Replay complete.";
        }
        ScrubPosition = _frames.Count > 1 ? _index * 100.0 / (_frames.Count - 1) : 0;
        ApplyFrame(_frames[_index]);
    }

    private void StopInternal()
    {
        IsPlaying = false;
        _playTimer.Stop();
        _index = 0;
        ScrubPosition = 0;
        TimeText = "--";
    }

    private void ApplyFrame(ReplayFrame frame)
    {
        TimeText = frame.Timestamp.LocalDateTime.ToString("HH:mm:ss.fff");
        FrameChanged?.Invoke(frame);
    }
}
