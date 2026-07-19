using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StarSensing.Dashboard.Services;

namespace StarSensing.Dashboard.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly GrpcClientService _grpcService;
    private readonly BearingStoreService _bearingStore = new();
    private readonly NetworkFilterManager _networkFilterManager;
    private readonly EnvironmentStreamService _environmentStream;
    private readonly SensingDataService _sensingData = new();
    private readonly CompassService _compass = new();
    private readonly TimeRangeService _timeRange = new();
    private readonly HistoricalTimelineService _timeline;

    [ObservableProperty]
    private string _connectionStatus = "Disconnected";

    [ObservableProperty]
    private bool _soundEnabled = true;

    public TimeRangeService TimeRange => _timeRange;
    public HistoricalTimelineService Timeline => _timeline;

    partial void OnSoundEnabledChanged(bool value) => SoundService.Enabled = value;

    public BearingStoreService BearingStore => _bearingStore;
    public CompassService Compass => _compass;

    public SignalMonitorViewModel SignalMonitorVM { get; }
    public HeatmapViewModel HeatmapVM { get; }
    public RadarViewModel RadarVM { get; }
    public MotionViewModel MotionVM { get; }
    public AreaMapViewModel AreaMapVM { get; }
    public NetworkOpsViewModel NetworkOpsVM { get; }
    public SolidViewViewModel SolidViewVM { get; }

    public MainViewModel()
    {
        _networkFilterManager = new NetworkFilterManager(_bearingStore);
        _grpcService = new GrpcClientService();
        _environmentStream = new EnvironmentStreamService(_networkFilterManager);
        _timeline = new HistoricalTimelineService(_sensingData, _timeRange);
        _compass.Initialize();
        _ = _bearingStore.InitializeAsync();

        ConnectCommand.Execute(null);

        SignalMonitorVM = new SignalMonitorViewModel(_grpcService, _networkFilterManager, _environmentStream, _bearingStore);
        HeatmapVM = new HeatmapViewModel(_grpcService, _networkFilterManager, _environmentStream, _bearingStore, _timeRange, _timeline, _sensingData);
        RadarVM = new RadarViewModel(_grpcService, _networkFilterManager, _environmentStream);
        MotionVM = new MotionViewModel(_environmentStream, _sensingData, _timeRange, _timeline);
        AreaMapVM = new AreaMapViewModel(_grpcService, _networkFilterManager, _environmentStream, _bearingStore, _compass, _timeline, _timeRange);
        NetworkOpsVM = new NetworkOpsViewModel();
        SolidViewVM = new SolidViewViewModel(_grpcService, _networkFilterManager, _environmentStream);

        _environmentStream.Start(_grpcService);
    }

    [RelayCommand]
    private void Connect()
    {
        try
        {
            _grpcService.Connect();
            ConnectionStatus = "Connected · Python ML pipeline active";
            _environmentStream.Start(_grpcService);
        }
        catch
        {
            ConnectionStatus = "Failed to connect";
        }
    }
}
