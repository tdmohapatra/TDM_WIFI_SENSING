using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StarSensing.Core.Protos;
using StarSensing.Dashboard.Models;
using StarSensing.Dashboard.Services;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;

namespace StarSensing.Dashboard.ViewModels;

public partial class MotionViewModel : ObservableObject
{
    private readonly EnvironmentStreamService _envStream;
    private readonly SensingDataService _data;
    private readonly TimeRangeService _timeRange;
    private readonly HistoricalTimelineService _timeline;
    private readonly DispatcherTimer _liveChartTimer;
    private readonly List<double> _liveConfidence = new();
    private const int LiveChartPoints = 120;

    [ObservableProperty] private double _motionConfidence;
    [ObservableProperty] private string _classification = "STATIC";
    [ObservableProperty] private int _activeApCount;
    [ObservableProperty] private int _sampleIntervalMs = 100;
    [ObservableProperty] private double _streamLatencyMs;
    [ObservableProperty] private string _lastUpdateText = "--";
    [ObservableProperty] private int _microSensitivity = 60;
    [ObservableProperty] private double _peakVariance;
    [ObservableProperty] private double _peakEntropy;
    [ObservableProperty] private double _stabilityIndex = 100;
    [ObservableProperty] private double _avgCrossCorrelation;
    [ObservableProperty] private double _lstmMotionConfidence;
    [ObservableProperty] private double _cnnActivityScore;
    [ObservableProperty] private string _replayStatus = "";
    [ObservableProperty] private bool _isReplayMode;
    [ObservableProperty] private bool _isPlaying;
    [ObservableProperty] private double _replayPosition;
    [ObservableProperty] private string _replayTimeText = "--";
    [ObservableProperty] private int _replayFrameCount;

    private DateTimeOffset _lastSynthEvent = DateTimeOffset.MinValue;

    public ObservableCollection<MotionEventMsg> Events { get; } = new();
    public ObservableCollection<SpatialZoneMsg> Zones { get; } = new();

    public TimeRangeService TimeRange => _timeRange;
    public HistoricalTimelineService Timeline => _timeline;

    public event Action<EnvironmentStateMsg>? OnStateReceived;
    public event Action<double>? OnReplayConfidence;
    public event Action<IReadOnlyList<(DateTimeOffset Time, double Confidence)>>? OnTimelineSeriesReady;

    public MotionViewModel(
        EnvironmentStreamService envStream,
        SensingDataService data,
        TimeRangeService timeRange,
        HistoricalTimelineService timeline)
    {
        _envStream = envStream;
        _data = data;
        _timeRange = timeRange;
        _timeline = timeline;
        _envStream.StateReceived += HandleState;
        _timeline.FrameChanged += ApplyHistoricalFrame;
        _timeline.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(HistoricalTimelineService.StatusText))
                ReplayStatus = _timeline.StatusText;
            if (e.PropertyName == nameof(HistoricalTimelineService.TimeText))
                ReplayTimeText = _timeline.TimeText;
            if (e.PropertyName == nameof(HistoricalTimelineService.FrameCount))
                ReplayFrameCount = _timeline.FrameCount;
            if (e.PropertyName == nameof(HistoricalTimelineService.IsPlaying))
                IsPlaying = _timeline.IsPlaying;
            if (e.PropertyName == nameof(HistoricalTimelineService.ScrubPosition))
                ReplayPosition = _timeline.ScrubPosition;
            if (e.PropertyName == nameof(HistoricalTimelineService.IsHistoricalActive))
                IsReplayMode = _timeline.IsHistoricalActive;
        };

        _liveChartTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _liveChartTimer.Tick += (_, _) => PushLiveChartPoint();
        _liveChartTimer.Start();

        _ = LoadEventsAsync();
    }

    partial void OnSampleIntervalMsChanged(int value) => _envStream.SetIntervalMs(value);

    partial void OnReplayPositionChanged(double value)
    {
        if (_timeline.IsPlaying) return;
        if (Math.Abs(_timeline.ScrubPosition - value) > 0.01)
            _timeline.ScrubPosition = value;
    }

    [RelayCommand]
    private async Task LoadReplayAsync() => await _timeline.ReloadAsync();

    [RelayCommand]
    private async Task StartReplayAsync() => await _timeline.PlayCommand.ExecuteAsync(null);

    [RelayCommand]
    private void PauseReplay() => _timeline.PauseCommand.Execute(null);

    [RelayCommand]
    private void StopReplay() => _timeline.StopCommand.Execute(null);

    [RelayCommand]
    private async Task SetLiveModeAsync()
    {
        _timeRange.SetLiveCommand.Execute(null);
        await _timeline.ReloadAsync();
    }

    [RelayCommand]
    private async Task SetHistoricalModeAsync()
    {
        _timeRange.SetHistoricalCommand.Execute(null);
        await _timeline.ReloadAsync();
    }

    private async Task LoadEventsAsync()
    {
        var stored = await _data.GetRecentMotionEventsAsync(50);
        Application.Current.Dispatcher.Invoke(() =>
        {
            Events.Clear();
            foreach (var e in stored)
                Events.Add(SensingDataService.ToProto(e));
        });
    }

    private void ApplyHistoricalFrame(ReplayFrame frame)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            MotionConfidence = frame.MotionConfidence;
            Classification = frame.Classification;
            ActiveApCount = frame.ActiveApCount;
            PeakVariance = frame.AvgVariance;
            PeakEntropy = frame.AvgEntropy;
            StabilityIndex = frame.StabilityIndex * 100;
            LstmMotionConfidence = frame.LstmMotionConfidence * 100;
            CnnActivityScore = frame.CnnActivityScore * 100;
            LastUpdateText = frame.Timestamp.LocalDateTime.ToString("HH:mm:ss.fff");

            Zones.Clear();
            foreach (var z in frame.Zones)
                Zones.Add(SensingDataService.ToProto(z));

            OnReplayConfidence?.Invoke(MotionConfidence);
        });
    }

    private void HandleState(EnvironmentStateMsg state)
    {
        if (_timeline.IsHistoricalActive) return;

        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            MotionConfidence = state.MotionConfidence;
            Classification = state.Classification.ToString().Replace('_', ' ');
            ActiveApCount = state.ActiveApCount;
            StabilityIndex = state.StabilityIndex * 100;
            LstmMotionConfidence = state.LstmMotionConfidence * 100;
            CnnActivityScore = state.CnnActivityScore * 100;
            LastUpdateText = (state.Timestamp?.ToDateTimeOffset() ?? DateTimeOffset.UtcNow).LocalDateTime.ToString("HH:mm:ss.fff");

            if (state.Signals.Count > 0)
            {
                PeakVariance = state.Signals.Max(s => s.Variance);
                PeakEntropy = state.Signals.Max(s => s.Entropy);
                AvgCrossCorrelation = state.Signals.Average(s => s.CrossCorrelation);
            }

            Zones.Clear();
            foreach (var z in state.Zones)
                Zones.Add(z);

            _liveConfidence.Add(state.MotionConfidence);
            while (_liveConfidence.Count > LiveChartPoints)
                _liveConfidence.RemoveAt(0);

            MaybeAddSyntheticEvent(state);
            OnStateReceived?.Invoke(state);
        }, DispatcherPriority.Background);
    }

    private void PushLiveChartPoint()
    {
        if (_timeline.IsHistoricalActive)
        {
            var series = _timeline.Frames
                .Select(f => (f.Timestamp, f.MotionConfidence))
                .ToList();
            OnTimelineSeriesReady?.Invoke(series);
            return;
        }

        if (_liveConfidence.Count == 0) return;
        var now = DateTimeOffset.UtcNow;
        var points = Enumerable.Range(0, _liveConfidence.Count)
            .Select(i => (now.AddMilliseconds(-(_liveConfidence.Count - i) * SampleIntervalMs), _liveConfidence[i]))
            .ToList();
        OnTimelineSeriesReady?.Invoke(points);
    }

    private void MaybeAddSyntheticEvent(EnvironmentStateMsg state)
    {
        if (state.MotionConfidence < MicroSensitivity / 100.0) return;
        if ((DateTimeOffset.UtcNow - _lastSynthEvent).TotalSeconds < 3) return;
        _lastSynthEvent = DateTimeOffset.UtcNow;
        Events.Insert(0, new MotionEventMsg
        {
            Description = $"{Classification} detected ({MotionConfidence:P0})",
            Confidence = state.MotionConfidence,
            Timestamp = state.Timestamp
        });
        while (Events.Count > 100) Events.RemoveAt(Events.Count - 1);
    }
}
