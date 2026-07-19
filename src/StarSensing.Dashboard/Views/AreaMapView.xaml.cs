using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using SkiaSharp;
using StarSensing.Core.Protos;
using StarSensing.Dashboard.Models;
using StarSensing.Dashboard.Services;
using StarSensing.Dashboard.ViewModels;

namespace StarSensing.Dashboard.Views;

public partial class AreaMapView : UserControl
{
    private const double MinViewportMeters = 0.1;
    private const double MaxViewportMeters = 60.0;
    private const double DefaultViewportMeters = 6.0;

    // ── 3D camera ──────────────────────────────────────────────────────
    private float _yaw;
    private float _elev = 0.62f;
    private bool _orbiting;
    private bool _panning;
    private System.Windows.Point _lastDrag;
    private float _panPx;
    private float _panPy;

    private string? _hoverBssid;
    private string? _hoverZoneId;
    private System.Windows.Point _hoverPoint;
    private bool _showDetailLabels = true;
    private bool _userAdjustedView;
    private bool _autoFitPending = true;
    private bool _motionOnlyMode;

    private readonly Dictionary<string, MapSignal> _signals = new();
    private readonly DispatcherTimer _renderTimer;
    private AreaMapViewModel? _viewModel;
    private double _viewportMeters = DefaultViewportMeters;
    private float _signalPhase;
    private double _motionConfidence;
    private EnvironmentClass _classification = EnvironmentClass.Static;
    private readonly List<SpatialZoneMsg> _zones = new();
    private readonly List<SKRect> _labelRects = new();

    /// <summary>UI element scale derived from zoom so strokes/text grow when zoomed in.</summary>
    private float UiScale => Math.Clamp((float)(DefaultViewportMeters / _viewportMeters), 0.6f, 2.6f);

    public AreaMapView()
    {
        InitializeComponent();
        _renderTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _renderTimer.Tick += (_, _) =>
        {
            _signalPhase = (_signalPhase + 0.04f) % 1f;
            MapCanvas.InvalidateVisual();
        };
        _renderTimer.Start();

        MapCanvas.MouseLeftButtonDown += OnMapMouseDown;
        MapCanvas.MouseLeftButtonUp += OnMapMouseUp;
        MapCanvas.MouseRightButtonDown += OnMapMouseRightDown;
        MapCanvas.MouseRightButtonUp += OnMapMouseRightUp;
        MapCanvas.PreviewMouseDown += OnMapPreviewMouseDown;
        MapCanvas.PreviewMouseUp += OnMapPreviewMouseUp;
        MapCanvas.MouseMove += OnMapMouseMove;
        MapCanvas.MouseLeave += (_, _) => { _hoverBssid = null; _hoverZoneId = null; MapCanvas.InvalidateVisual(); };
        MapCanvas.PreviewMouseLeftButtonDown += OnMapPreviewLeftDown;

        DataContextChanged += OnDataContextChanged;
    }

    private bool _bearingDrag;
    private string? _bearingDragBssid;

    private void OnMapMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && _viewModel != null)
        {
            var p = e.GetPosition(MapCanvas);
            var hit = HitTestSignal(p);
            if (hit != null)
            {
                _bearingDrag = true;
                _bearingDragBssid = hit.Bssid;
                _viewModel.SetCalibrationTargetByBssid(hit.Bssid);
                _viewModel.CalibrationMode = true;
                MapCanvas.CaptureMouse();
                e.Handled = true;
            }
            return;
        }

        if (_viewModel?.CalibrationMode == true)
        {
            var p = e.GetPosition(MapCanvas);
            var hit = HitTestSignal(p);
            _viewModel.SetCalibrationTargetByBssid(hit?.Bssid);
            MapCanvas.InvalidateVisual();
            return;
        }

        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            StartPan(e);
            return;
        }

        _orbiting = true;
        _lastDrag = e.GetPosition(MapCanvas);
        MapCanvas.CaptureMouse();
    }

    private void OnMapMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
        {
            _orbiting = false;
            if (_bearingDrag)
            {
                _bearingDrag = false;
                if (_viewModel != null && !string.IsNullOrEmpty(_bearingDragBssid))
                    _ = _viewModel.SetBearingFromMapAsync(_bearingDragBssid, _viewModel.CalibrationBearing);
                _bearingDragBssid = null;
            }
        }
        if (!_orbiting && !_panning && !_bearingDrag)
            MapCanvas.ReleaseMouseCapture();
    }

    private void OnMapMouseRightDown(object sender, MouseButtonEventArgs e) => StartPan(e);

    private void OnMapMouseRightUp(object sender, MouseButtonEventArgs e)
    {
        _panning = false;
        if (!_orbiting && !_panning)
            MapCanvas.ReleaseMouseCapture();
    }

    private void OnMapMouseMiddleDown(object sender, MouseButtonEventArgs e) => StartPan(e);

    private void OnMapMouseMiddleUp(object sender, MouseButtonEventArgs e)
    {
        _panning = false;
        if (!_orbiting && !_panning)
            MapCanvas.ReleaseMouseCapture();
    }

    private void OnMapPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Middle)
        {
            StartPan(e);
            e.Handled = true;
        }
    }

    private void OnMapPreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Middle)
            OnMapMouseMiddleUp(sender, e);
    }

    private void StartPan(MouseButtonEventArgs e)
    {
        _panning = true;
        _lastDrag = e.GetPosition(MapCanvas);
        MapCanvas.CaptureMouse();
        e.Handled = true;
    }

    private void OnMapMouseMove(object sender, MouseEventArgs e)
    {
        var p = e.GetPosition(MapCanvas);

        if (_bearingDrag && _viewModel != null && !string.IsNullOrEmpty(_bearingDragBssid))
        {
            ApplyBearingFromMouse(p, _bearingDragBssid);
            return;
        }

        if (_orbiting)
        {
            _yaw += (float)(p.X - _lastDrag.X) * 0.01f;
            _elev = Math.Clamp(_elev + (float)(p.Y - _lastDrag.Y) * 0.01f, 0.12f, 1.55f);
            _lastDrag = p;
            _userAdjustedView = true;
            MapCanvas.InvalidateVisual();
            return;
        }

        if (_panning)
        {
            _panPx += (float)(p.X - _lastDrag.X);
            _panPy += (float)(p.Y - _lastDrag.Y);
            _lastDrag = p;
            _userAdjustedView = true;
            MapCanvas.InvalidateVisual();
            return;
        }

        UpdateHoverTarget(p);
    }

    private (SKPoint Pt, float Depth) Project3DWorld(float worldX, float worldZ, float heightM, float cx, float cy, float ppm)
    {
        float cyaw = MathF.Cos(_yaw), syaw = MathF.Sin(_yaw);
        float xr = worldX * cyaw - worldZ * syaw;
        float zr = worldX * syaw + worldZ * cyaw;

        float se = MathF.Sin(_elev), ce = MathF.Cos(_elev);
        float sx = cx + _panPx + xr * ppm;
        float sy = cy + _panPy - (zr * se + heightM * ce) * ppm;
        float depth = zr * ce - heightM * se;
        return (new SKPoint(sx, sy), depth);
    }

    /// <summary>Projects compass bearing (0=N) to screen via 3D camera.</summary>
    private (SKPoint Pt, float Depth) Project3D(float bearingDeg, float radiusM, float heightM, float cx, float cy, float ppm)
    {
        var (east, north) = BearingStoreService.PolarToMeters(bearingDeg, radiusM);
        return Project3DWorld((float)east, (float)north, heightM, cx, cy, ppm);
    }

    /// <summary>Greedy label-collision avoidance: stacks into free vertical slots near the anchor or skips drawing.</summary>
    private bool TryPlaceLabel(SKFont font, string txt, float anchorX, float anchorY, SKTextAlign align, out SKPoint pos)
    {
        float width = font.MeasureText(txt);
        float lineH = font.Size + 4f;
        for (int attempt = 0; attempt < 6; attempt++)
        {
            float oy = anchorY + attempt * lineH;
            float left = align switch { SKTextAlign.Center => anchorX - width / 2f, SKTextAlign.Right => anchorX - width, _ => anchorX };
            var rect = SKRect.Create(left - 2f, oy - lineH, width + 4f, lineH);
            bool overlap = false;
            foreach (var r in _labelRects)
                if (rect.Left < r.Right && rect.Right > r.Left && rect.Top < r.Bottom && rect.Bottom > r.Top) { overlap = true; break; }
            if (!overlap) { _labelRects.Add(rect); pos = new SKPoint(anchorX, oy); return true; }
        }
        pos = default; return false;
    }

    private (float wx, float wz, float trueDist, float bearingDeg, bool calibrated, bool offRange) GetWorldPosition(SelectableNetwork signal)
    {
        float trueDistance = MathF.Max(0.05f, (float)signal.DistanceMeters);
        float bearingDeg = (float)signal.BearingDegrees;
        bool calibrated = signal.BearingSource is "manual" or "compass" or "location";
        var (east, north) = BearingStoreService.PolarToMeters(bearingDeg, trueDistance);
        float maxR = (float)(_viewportMeters * 0.96);
        float dist2d = MathF.Sqrt((float)(east * east + north * north));
        if (dist2d > maxR && dist2d > 0.01f)
        {
            float scale = maxR / dist2d;
            return ((float)(east * scale), (float)(north * scale), trueDistance, bearingDeg, calibrated, true);
        }

        return ((float)east, (float)north, trueDistance, bearingDeg, calibrated, false);
    }

    private void AutoFitToSignals(IReadOnlyList<SelectableNetwork> signals)
    {
        if (signals.Count == 0) return;

        float maxExtent = 0.5f;
        foreach (var s in signals)
        {
            var (_, _, trueDist, _, _, _) = GetWorldPosition(s);
            maxExtent = MathF.Max(maxExtent, trueDist);
        }

        _viewportMeters = Math.Clamp(maxExtent * 1.35, MinViewportMeters, MaxViewportMeters);
        _panPx = 0;
        _panPy = 0;
        _autoFitPending = false;
    }

    private void OnMapPreviewLeftDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount >= 2)
        {
            _userAdjustedView = false;
            _autoFitPending = true;
            _orbiting = false;
            MapCanvas.InvalidateVisual();
            e.Handled = true;
        }
    }

    private void OnDataContextChanged(object sender, System.Windows.DependencyPropertyChangedEventArgs e)
    {
        if (_viewModel != null)
        {
            _viewModel.OnMeasurementsReceived -= ViewModel_OnMeasurementsReceived;
            _viewModel.OnStateReceived -= ViewModel_OnStateReceived;
            _viewModel.PositionsUpdated -= ViewModel_PositionsUpdated;
            _viewModel.SelectedNetworks.CollectionChanged -= SelectedNetworks_CollectionChanged;
            foreach (var network in _viewModel.Networks)
                network.PropertyChanged -= Network_PropertyChanged;
        }

        _viewModel = e.NewValue as AreaMapViewModel;

        if (_viewModel != null)
        {
            _viewModel.OnMeasurementsReceived += ViewModel_OnMeasurementsReceived;
            _viewModel.OnStateReceived += ViewModel_OnStateReceived;
            _viewModel.PositionsUpdated += ViewModel_PositionsUpdated;
            _viewModel.SelectedNetworks.CollectionChanged += SelectedNetworks_CollectionChanged;
            foreach (var network in _viewModel.Networks)
                network.PropertyChanged += Network_PropertyChanged;
        }
    }

    private void ViewModel_PositionsUpdated()
    {
        if (!_userAdjustedView)
            _autoFitPending = true;
        MapCanvas.InvalidateVisual();
    }

    private void SelectedNetworks_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems != null)
        {
            foreach (SelectableNetwork network in e.NewItems)
                network.PropertyChanged += Network_PropertyChanged;
        }

        if (e.OldItems != null)
        {
            foreach (SelectableNetwork network in e.OldItems)
                network.PropertyChanged -= Network_PropertyChanged;
        }

        MapCanvas.InvalidateVisual();
    }

    private void Network_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SelectableNetwork.IsSelected)
            or nameof(SelectableNetwork.LatestRssi)
            or nameof(SelectableNetwork.Variance)
            or nameof(SelectableNetwork.Entropy)
            or nameof(SelectableNetwork.ChangeRate)
            or nameof(SelectableNetwork.MotionConfidence)
            or nameof(SelectableNetwork.BearingDegrees))
            MapCanvas.InvalidateVisual();
    }

    private List<SelectableNetwork> GetVisibleSignals()
    {
        if (_viewModel == null)
            return new List<SelectableNetwork>();

        return _viewModel.SelectedSignalSource switch
        {
            SignalSourceMode.All => _viewModel.Networks
                .OrderBy(n => n.DistanceMeters)
                .ToList(),
            SignalSourceMode.Connected => _viewModel.Networks
                .Where(_viewModel.IsConnectedNetwork)
                .OrderBy(n => n.DistanceMeters)
                .ToList(),
            _ => _viewModel.SelectedNetworks
                .Where(n => n.IsSelected)
                .OrderBy(n => n.DistanceMeters)
                .ToList()
        };
    }

    private SelectableNetwork? HitTestSignal(System.Windows.Point mouse)
    {
        if (_viewModel == null) return null;

        float w = (float)MapCanvas.ActualWidth;
        float h = (float)MapCanvas.ActualHeight;
        if (w < 10 || h < 10) return null;

        float cx = w / 2f;
        float cy = h / 2f;
        float scale = Math.Min(w, h) * 0.46f;
        float ppm = scale / (float)_viewportMeters;
        float hitR = 18f * UiScale;

        SelectableNetwork? bestNet = null;
        float best = hitR * hitR;

        foreach (var signal in GetVisibleSignals())
        {
            var (wx, wz, _, _, _, _) = GetWorldPosition(signal);
            float strength = Math.Clamp((signal.LatestRssi + 100f) / 80f, 0.05f, 1f);
            float heightM = (0.12f + strength * 0.4f) * (float)_viewportMeters;
            var (top, _) = Project3DWorld(wx, wz, heightM, cx, cy, ppm);
            float dx = (float)mouse.X - top.X;
            float dy = (float)mouse.Y - top.Y;
            float d2 = dx * dx + dy * dy;
            if (d2 < best)
            {
                best = d2;
                bestNet = signal;
            }
        }

        return bestNet;
    }

    private void ApplyBearingFromMouse(System.Windows.Point mouse, string bssid)
    {
        if (_viewModel == null) return;

        float w = (float)MapCanvas.ActualWidth;
        float h = (float)MapCanvas.ActualHeight;
        float cx = w / 2f;
        float cy = h / 2f;
        float scale = Math.Min(w, h) * 0.46f;
        float ppm = scale / (float)_viewportMeters;
        var (center, _) = Project3DWorld(0, 0, 0, cx, cy, ppm);

        float dx = (float)mouse.X - center.X;
        float dy = center.Y - (float)mouse.Y;
        double bearing = BearingStoreService.NormalizeDeg(Math.Atan2(dx, dy) * 180.0 / Math.PI);

        _viewModel.CalibrationBearing = bearing;
        var net = _viewModel.GetNetwork(bssid);
        net?.SetBearing(bearing, "manual");
        MapCanvas.InvalidateVisual();
    }

    private void DrawDirectionRays(SKCanvas canvas, float cx, float cy, float scale, SKPoint you, IReadOnlyList<SelectableNetwork> signals)
    {
        float ppm = scale / (float)_viewportMeters;
        float ui = UiScale;

        foreach (var signal in signals.Take(40))
        {
            var (_, _, dist, bearing, calibrated, _) = GetWorldPosition(signal);
            var (edge, _) = Project3D(bearing, Math.Min(dist, (float)_viewportMeters), 0, cx, cy, ppm);
            SKColor color = calibrated
                ? SKColor.Parse("#43e3a0").WithAlpha(140)
                : SKColor.Parse("#4361ee").WithAlpha(70);

            using var ray = new SKPaint
            {
                Color = color,
                StrokeWidth = (calibrated ? 2f : 1f) * ui,
                IsAntialias = true,
                PathEffect = calibrated ? null : SKPathEffect.CreateDash(new[] { 6f * ui, 4f * ui }, 0)
            };
            canvas.DrawLine(you, edge, ray);

            if (string.Equals(signal.Bssid, _viewModel?.CalibrationTarget?.Bssid, StringComparison.OrdinalIgnoreCase))
            {
                using var highlight = new SKPaint
                {
                    Color = SKColor.Parse("#f5a623").WithAlpha(200),
                    StrokeWidth = 3f * ui,
                    IsAntialias = true
                };
                canvas.DrawLine(you, edge, highlight);
            }
        }
    }

    private void UpdateHoverTarget(System.Windows.Point mouse)
    {
        if (_viewModel == null) return;

        float w = (float)MapCanvas.ActualWidth;
        float h = (float)MapCanvas.ActualHeight;
        if (w < 10 || h < 10) return;

        float cx = w / 2f;
        float cy = h / 2f;
        float scale = Math.Min(w, h) * 0.46f;
        float ppm = scale / (float)_viewportMeters;
        float ui = UiScale;
        float hitR = 22f * ui;

        string? zoneHit = null;
        string? apHit = null;
        float best = hitR * hitR;

        if (_motionOnlyMode)
        {
            foreach (var z in _zones)
            {
                var center = GetZoneScreenCenter(z, cx, cy, ppm);
                float dx = (float)mouse.X - center.X;
                float dy = (float)mouse.Y - center.Y;
                float d2 = dx * dx + dy * dy;
                if (d2 < best)
                {
                    best = d2;
                    zoneHit = z.ZoneId;
                }
            }

            if (!string.Equals(_hoverZoneId, zoneHit, StringComparison.OrdinalIgnoreCase))
            {
                _hoverZoneId = zoneHit;
                _hoverBssid = null;
                _hoverPoint = mouse;
                MapCanvas.InvalidateVisual();
            }
            else if (zoneHit != null)
            {
                _hoverPoint = mouse;
            }

            return;
        }

        var signals = GetVisibleSignals();
        best = hitR * hitR;

        foreach (var signal in signals)
        {
            var (wx, wz, _, _, _, _) = GetWorldPosition(signal);
            float strength = Math.Clamp((signal.LatestRssi + 100f) / 80f, 0.05f, 1f);
            float heightM = (0.12f + strength * 0.4f) * (float)_viewportMeters;
            var (top, _) = Project3DWorld(wx, wz, heightM, cx, cy, ppm);
            float dx = (float)mouse.X - top.X;
            float dy = (float)mouse.Y - top.Y;
            float d2 = dx * dx + dy * dy;
            if (d2 < best)
            {
                best = d2;
                apHit = signal.Bssid;
            }
        }

        if (!string.Equals(_hoverBssid, apHit, StringComparison.OrdinalIgnoreCase))
        {
            _hoverBssid = apHit;
            _hoverZoneId = null;
            _hoverPoint = mouse;
            MapCanvas.InvalidateVisual();
        }
        else if (apHit != null)
        {
            _hoverPoint = mouse;
        }
    }

    private SKPoint GetZoneScreenCenter(SpatialZoneMsg z, float cx, float cy, float ppm)
    {
        float mapSpan = (float)(_viewportMeters * 2.0);
        float east = (float)((z.X - 0.5) * mapSpan);
        float north = (float)((z.Y - 0.5) * mapSpan);
        var (center, _) = Project3DWorld(east, north, 0.04f * (float)_viewportMeters, cx, cy, ppm);
        return center;
    }

    private void AutoFitToZones()
    {
        if (_zones.Count == 0) return;

        float mapSpan = (float)(_viewportMeters * 2.0);
        float maxExtent = 1.2f;
        foreach (var z in _zones)
        {
            float east = MathF.Abs((float)((z.X - 0.5) * mapSpan));
            float north = MathF.Abs((float)((z.Y - 0.5) * mapSpan));
            float zoneR = MathF.Max(0.35f, (float)(z.Radius * _viewportMeters * 0.45));
            maxExtent = MathF.Max(maxExtent, MathF.Max(east, north) + zoneR);
        }

        _viewportMeters = Math.Clamp(maxExtent * 1.45, MinViewportMeters, MaxViewportMeters);
        _panPx = 0;
        _panPy = 0;
        _autoFitPending = false;
    }

    private void ViewModel_OnMeasurementsReceived(MeasurementBatch batch)
    {
        foreach (var m in batch.Measurements)
        {
            if (!_signals.TryGetValue(m.Bssid, out var signal))
            {
                signal = new MapSignal(m.Bssid, m.Ssid);
                _signals[m.Bssid] = signal;
            }

            signal.Ssid = string.IsNullOrWhiteSpace(m.Ssid) ? signal.Ssid : m.Ssid;
            signal.Rssi = m.RssiDbm;
            signal.SignalQuality = m.SignalQuality;
            signal.Channel = m.Channel;
            signal.DistanceMeters = EstimateDistanceMeters(m.RssiDbm);
            signal.MotionConfidence = 0;
        }

        MapCanvas.InvalidateVisual();
    }

    private void ViewModel_OnStateReceived(EnvironmentStateMsg state)
    {
        _motionConfidence = state.MotionConfidence;
        _classification = state.Classification;

        _zones.Clear();
        _zones.AddRange(state.Zones);

        foreach (var sig in state.Signals)
        {
            if (!_signals.TryGetValue(sig.Bssid, out var signal))
            {
                signal = new MapSignal(sig.Bssid, sig.Ssid);
                _signals[sig.Bssid] = signal;
            }

            signal.Ssid = string.IsNullOrWhiteSpace(sig.Ssid) ? signal.Ssid : sig.Ssid;
            signal.Rssi = sig.RawRssi;
            signal.DistanceMeters = EstimateDistanceMeters(sig.RawRssi);
            signal.MotionConfidence = Math.Max(sig.MotionConfidence, Math.Min(1.0, sig.Variance / 8.0));
            signal.Variance = sig.Variance;
        }

        MapCanvas.InvalidateVisual();
    }

    private void OnPaintMap(object sender, SkiaSharp.Views.Desktop.SKPaintSurfaceEventArgs e)
    {
        var canvas = e.Surface.Canvas;
        float w = e.Info.Width;
        float h = e.Info.Height;
        canvas.Clear(SKColor.Parse("#0a0e27"));
        _labelRects.Clear();

        float cx = w / 2f;
        float cy = h / 2f;
        float scale = Math.Min(w, h) * 0.46f;

        var visible = GetVisibleSignals();
        if (_autoFitPending)
        {
            if (_motionOnlyMode && _zones.Count > 0)
                AutoFitToZones();
            else if (visible.Count > 0)
                AutoFitToSignals(visible);
        }

        float ppm = scale / (float)_viewportMeters;
        var (you, _) = Project3DWorld(0, 0, 0, cx, cy, ppm);

        DrawGround(canvas, cx, cy, scale);
        if (_motionOnlyMode)
        {
            DrawMotionHotspots(canvas, cx, cy, scale, visible);
            DrawMotionZones(canvas, cx, cy, scale);
            DrawYouMotionAura(canvas, you);
        }
        else
        {
            DrawZones(canvas, cx, cy, scale);
            DrawDirectionRays(canvas, cx, cy, scale, you, visible);
            DrawSignalLinks(canvas, cx, cy, scale, you);
        }

        DrawYou(canvas, you);
        DrawCompass(canvas, cx, cy, scale);
        DrawLegend(canvas, w, h);
        if (_motionOnlyMode)
            DrawMotionHoverTooltip(canvas, w, h);
        else
            DrawHoverTooltip(canvas, w, h);
    }

    private void DrawMotionHoverTooltip(SKCanvas canvas, float w, float h)
    {
        if (string.IsNullOrEmpty(_hoverZoneId)) return;

        var zone = _zones.FirstOrDefault(z =>
            string.Equals(z.ZoneId, _hoverZoneId, StringComparison.OrdinalIgnoreCase));
        if (zone == null) return;

        float ui = UiScale;
        float plateW = 240f * ui;
        float plateH = 82f * ui;
        float x = Math.Clamp((float)_hoverPoint.X + 14f * ui, 8f, w - plateW - 8f);
        float y = Math.Clamp((float)_hoverPoint.Y - plateH - 10f * ui, 8f, h - plateH - 8f);

        float mapSpan = (float)(_viewportMeters * 2.0);
        float eastM = (float)((zone.X - 0.5) * mapSpan);
        float northM = (float)((zone.Y - 0.5) * mapSpan);
        float distM = MathF.Sqrt(eastM * eastM + northM * northM);
        float bearing = (float)BearingStoreService.NormalizeDeg(Math.Atan2(eastM, northM) * 180.0 / Math.PI);

        DrawLabelPlate(canvas, x, y, plateW, plateH);
        using var titleFont = new SKFont(SKTypeface.Default, 12f * ui) { Embolden = true };
        using var bodyFont = new SKFont(SKTypeface.Default, 10f * ui);
        using var titlePaint = new SKPaint { Color = SKColors.White.WithAlpha(235), IsAntialias = true };
        using var bodyPaint = new SKPaint { Color = SKColor.Parse("#8b95c9"), IsAntialias = true };
        using var motPaint = new SKPaint { Color = SKColor.Parse("#f5a623"), IsAntialias = true };

        canvas.DrawText(zone.Name, x + 10 * ui, y + 18 * ui, SKTextAlign.Left, titleFont, titlePaint);
        canvas.DrawText($"{distM:F1} m · {bearing:F0}° from you",
            x + 10 * ui, y + 34 * ui, SKTextAlign.Left, bodyFont, bodyPaint);
        canvas.DrawText($"Motion {zone.MotionConfidence * 100:F0}% · Occupancy {zone.OccupancyConfidence * 100:F0}%",
            x + 10 * ui, y + 50 * ui, SKTextAlign.Left, bodyFont, motPaint);
        canvas.DrawText($"Radius ~{(zone.Radius * _viewportMeters * 0.45):F1} m · Live",
            x + 10 * ui, y + 64 * ui, SKTextAlign.Left, bodyFont, bodyPaint);
    }

    private void DrawHoverTooltip(SKCanvas canvas, float w, float h)
    {
        if (_viewModel == null || string.IsNullOrEmpty(_hoverBssid))
            return;

        var signal = GetVisibleSignals().FirstOrDefault(s =>
            string.Equals(s.Bssid, _hoverBssid, StringComparison.OrdinalIgnoreCase));
        if (signal == null)
            return;

        var (_, _, trueDistance, bearingDeg, calibrated, offRange) = GetWorldPosition(signal);
        string name = string.IsNullOrWhiteSpace(signal.Ssid) ? signal.Bssid : signal.Ssid;
        if (name.Length > 24) name = name[..23] + "…";

        float ui = UiScale;
        float plateW = 250f * ui;
        float plateH = 88f * ui;
        float x = Math.Clamp((float)_hoverPoint.X + 14f * ui, 8f, w - plateW - 8f);
        float y = Math.Clamp((float)_hoverPoint.Y - plateH - 10f * ui, 8f, h - plateH - 8f);

        DrawLabelPlate(canvas, x, y, plateW, plateH);
        using var titleFont = new SKFont(SKTypeface.Default, 12f * ui) { Embolden = true };
        using var bodyFont = new SKFont(SKTypeface.Default, 10f * ui);
        using var titlePaint = new SKPaint { Color = SKColors.White.WithAlpha(235), IsAntialias = true };
        using var bodyPaint = new SKPaint { Color = SKColor.Parse("#8b95c9"), IsAntialias = true };

        string posTag = calibrated ? "calibrated" : "estimated";
        string rangeTag = offRange ? " · beyond view" : "";
        canvas.DrawText(name, x + 10 * ui, y + 18 * ui, SKTextAlign.Left, titleFont, titlePaint);
        canvas.DrawText($"{trueDistance:F1} m · {bearingDeg:F0}° · {signal.LatestRssi} dBm · Q {signal.SignalQuality}%{rangeTag}",
            x + 10 * ui, y + 34 * ui, SKTextAlign.Left, bodyFont, bodyPaint);
        canvas.DrawText($"Var {signal.Variance:F1} · Ent {signal.Entropy:F1} · Motion {signal.MotionConfidence * 100:F0}% · {posTag}",
            x + 10 * ui, y + 50 * ui, SKTextAlign.Left, bodyFont, bodyPaint);
        canvas.DrawText($"{signal.BandChannelText} · Chg {signal.ChangeRate:F2} · {_viewModel.TimeFrameText}",
            x + 10 * ui, y + 66 * ui, SKTextAlign.Left, bodyFont, bodyPaint);
        canvas.DrawText(signal.Bssid, x + 10 * ui, y + 80 * ui, SKTextAlign.Left, bodyFont, bodyPaint);
    }

    private void DrawMotionZones(SKCanvas canvas, float cx, float cy, float scale)
    {
        if (_zones.Count == 0) return;
        float ppm = scale / (float)_viewportMeters;
        float mapSpan = (float)(_viewportMeters * 2.0);
        float ui = UiScale;

        foreach (var z in _zones.OrderByDescending(z => z.MotionConfidence))
        {
            float east = (float)((z.X - 0.5) * mapSpan);
            float north = (float)((z.Y - 0.5) * mapSpan);
            float zoneRadiusM = MathF.Max(0.35f, (float)(z.Radius * _viewportMeters * 0.45));
            float motion = (float)Math.Clamp(z.MotionConfidence, 0, 1);
            float occ = (float)Math.Clamp(z.OccupancyConfidence, 0, 1);

            SKColor motionColor = SKColor.Parse("#f59e0b");
            SKColor occColor = SKColor.Parse("#00f5d4");

            // Ground motion disc — size and alpha = motion strength
            using var disc = new SKPaint
            {
                Color = motionColor.WithAlpha((byte)Math.Clamp(40 + motion * 160, 40, 200)),
                IsAntialias = true,
                MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 12f * ui * (0.5f + motion))
            };
            var (floor, _) = Project3DWorld(east, north, 0, cx, cy, ppm);
            canvas.DrawCircle(floor, zoneRadiusM * ppm * (0.8f + motion * 0.5f), disc);

            // Occupancy ring on ground
            using var occStroke = new SKPaint
            {
                Color = occColor.WithAlpha((byte)Math.Clamp(60 + occ * 140, 60, 200)),
                Style = SKPaintStyle.Stroke,
                StrokeWidth = (1.5f + occ * 2f) * ui,
                IsAntialias = true
            };
            using var occPath = new SKPath();
            for (int i = 0; i <= 48; i++)
            {
                float a = i * (360f / 48f) * MathF.PI / 180f;
                float dx = east + zoneRadiusM * MathF.Sin(a);
                float dz = north + zoneRadiusM * MathF.Cos(a);
                var (p, _) = Project3DWorld(dx, dz, 0, cx, cy, ppm);
                if (i == 0) occPath.MoveTo(p); else occPath.LineTo(p);
            }
            occPath.Close();
            canvas.DrawPath(occPath, occStroke);

            // Motion strength column (height = motion %)
            float colH = (0.15f + motion * 0.85f) * (float)_viewportMeters;
            var (basePt, _) = Project3DWorld(east, north, 0, cx, cy, ppm);
            var (topPt, _) = Project3DWorld(east, north, colH, cx, cy, ppm);
            using var colPaint = new SKPaint
            {
                Color = motionColor.WithAlpha((byte)Math.Clamp(100 + motion * 155, 100, 255)),
                StrokeWidth = (6f + motion * 10f) * ui,
                IsAntialias = true,
                StrokeCap = SKStrokeCap.Round
            };
            canvas.DrawLine(basePt, topPt, colPaint);

            // Pulsing motion rings
            for (int r = 0; r < 3; r++)
            {
                float phase = (_signalPhase + r * 0.33f) % 1f;
                float pulseR = zoneRadiusM * (0.5f + phase * 0.8f);
                using var pulse = new SKPaint
                {
                    Color = motionColor.WithAlpha((byte)((1f - phase) * motion * 180)),
                    Style = SKPaintStyle.Stroke,
                    StrokeWidth = 2f * ui,
                    IsAntialias = true
                };
                using var pulsePath = new SKPath();
                for (int i = 0; i <= 32; i++)
                {
                    float a = i * (360f / 32f) * MathF.PI / 180f;
                    float dx = east + pulseR * MathF.Sin(a);
                    float dz = north + pulseR * MathF.Cos(a);
                    var (p, _) = Project3DWorld(dx, dz, 0.02f * (float)_viewportMeters, cx, cy, ppm);
                    if (i == 0) pulsePath.MoveTo(p); else pulsePath.LineTo(p);
                }
                pulsePath.Close();
                canvas.DrawPath(pulsePath, pulse);
            }

            var (center, _) = Project3DWorld(east, north, colH + 0.05f * (float)_viewportMeters, cx, cy, ppm);
            using var font = new SKFont(SKTypeface.Default, 11f * ui) { Embolden = true };
            using var text = new SKPaint { Color = SKColors.White.WithAlpha(240), IsAntialias = true };
            string zoneLine = $"{z.Name} · Motion {motion * 100:F0}% · Occ {occ * 100:F0}%";
            if (TryPlaceLabel(font, zoneLine, center.X + 8 * ui, center.Y - 6 * ui, SKTextAlign.Left, out var zlp))
                canvas.DrawText(zoneLine, zlp.X, zlp.Y, SKTextAlign.Left, font, text);
        }
    }

    private void DrawMotionHotspots(SKCanvas canvas, float cx, float cy, float scale, IReadOnlyList<SelectableNetwork> signals)
    {
        float ppm = scale / (float)_viewportMeters;
        float ui = UiScale;

        foreach (var signal in signals)
        {
            float motion = (float)Math.Clamp(Math.Max(signal.MotionConfidence, signal.Variance / 8.0), 0, 1);
            if (motion < 0.08) continue;

            var (wx, wz, dist, _, _, _) = GetWorldPosition(signal);
            var (floor, _) = Project3DWorld(wx, wz, 0, cx, cy, ppm);
            float radius = (8f + motion * 28f) * ui;

            using var glow = new SKPaint
            {
                Color = SKColor.Parse("#f59e0b").WithAlpha((byte)Math.Clamp(30 + motion * 120, 30, 150)),
                IsAntialias = true,
                MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 10f * ui)
            };
            canvas.DrawCircle(floor, radius, glow);

            using var core = new SKPaint
            {
                Color = SKColor.Parse("#ef4444").WithAlpha((byte)Math.Clamp(80 + motion * 175, 80, 255)),
                IsAntialias = true
            };
            canvas.DrawCircle(floor, 3f + motion * 6f * ui, core);

            float spikeH = (0.05f + motion * 0.35f) * (float)_viewportMeters;
            var (spikeTop, _) = Project3DWorld(wx, wz, spikeH, cx, cy, ppm);
            using var spike = new SKPaint
            {
                Color = SKColor.Parse("#f59e0b").WithAlpha((byte)Math.Clamp(100 + motion * 155, 100, 255)),
                StrokeWidth = (2f + motion * 4f) * ui,
                IsAntialias = true,
                StrokeCap = SKStrokeCap.Round
            };
            canvas.DrawLine(floor, spikeTop, spike);
        }
    }

    private void DrawYouMotionAura(SKCanvas canvas, SKPoint you)
    {
        float ui = UiScale;
        float motion = (float)Math.Clamp(_motionConfidence, 0, 1);
        if (motion < 0.05) return;

        float pulse = (20f + motion * 40f) * ui * (0.85f + 0.15f * MathF.Sin(_signalPhase * MathF.PI * 2f));
        using var aura = new SKPaint
        {
            Color = SKColor.Parse("#00f5d4").WithAlpha((byte)Math.Clamp(40 + motion * 100, 40, 140)),
            IsAntialias = true,
            MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 14f * ui)
        };
        canvas.DrawCircle(you, pulse, aura);
    }

    private void DrawZones(SKCanvas canvas, float cx, float cy, float scale)
    {
        if (_zones.Count == 0) return;
        float ppm = scale / (float)_viewportMeters;
        float mapSpan = (float)(_viewportMeters * 2.0);
        float ui = UiScale;

        foreach (var z in _zones.OrderByDescending(z => z.OccupancyConfidence))
        {
            float east = (float)((z.X - 0.5) * mapSpan);
            float north = (float)((z.Y - 0.5) * mapSpan);
            float zoneRadiusM = MathF.Max(0.35f, (float)(z.Radius * _viewportMeters * 0.45));

            SKColor color;
            try { color = SKColor.Parse(z.Color); }
            catch { color = SKColor.Parse("#4361ee"); }

            byte fillAlpha = (byte)Math.Clamp(35 + z.OccupancyConfidence * 90, 35, 160);
            using var fill = new SKPaint
            {
                Color = color.WithAlpha(fillAlpha),
                Style = SKPaintStyle.Fill,
                IsAntialias = true
            };
            using var stroke = new SKPaint
            {
                Color = color.WithAlpha(200),
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 2f * ui,
                IsAntialias = true
            };

            using var path = new SKPath();
            for (int i = 0; i <= 40; i++)
            {
                float a = i * (360f / 40f) * MathF.PI / 180f;
                float dx = east + zoneRadiusM * MathF.Sin(a);
                float dz = north + zoneRadiusM * MathF.Cos(a);
                var (p, _) = Project3DWorld(dx, dz, 0, cx, cy, ppm);
                if (i == 0) path.MoveTo(p); else path.LineTo(p);
            }
            path.Close();
            canvas.DrawPath(path, fill);
            canvas.DrawPath(path, stroke);

            var (center, _) = Project3DWorld(east, north, 0.04f * (float)_viewportMeters, cx, cy, ppm);
            if (_showDetailLabels || z.OccupancyConfidence > 0.15)
            {
                using var font = new SKFont(SKTypeface.Default, 10f * ui) { Embolden = true };
                using var text = new SKPaint { Color = SKColors.White.WithAlpha(230), IsAntialias = true };
                canvas.DrawText($"{z.Name}", center.X + 6 * ui, center.Y - 8 * ui, SKTextAlign.Left, font, text);
                using var smallFont = new SKFont(SKTypeface.Default, 9f * ui);
                canvas.DrawText($"Occ {z.OccupancyConfidence * 100:F0}%  Mot {z.MotionConfidence * 100:F0}%",
                    center.X + 6 * ui, center.Y + 6 * ui, SKTextAlign.Left, smallFont, text);
            }
        }
    }

    /// <summary>
    /// Radial ground plane: concentric distance rings labelled in metres, plus
    /// compass spokes. Screen radius is strictly proportional to true distance.
    /// </summary>
    private void DrawGround(SKCanvas canvas, float cx, float cy, float scale)
    {
        float ppm = scale / (float)_viewportMeters;
        float ui = UiScale;

        using var ringPaint = new SKPaint
        {
            Color = SKColor.Parse("#00f5d4").WithAlpha(85),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1.4f * ui,
            IsAntialias = true
        };
        using var spokePaint = new SKPaint
        {
            Color = SKColor.Parse("#4361ee").WithAlpha(75),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1.1f * ui,
            IsAntialias = true
        };
        using var minorGrid = new SKPaint
        {
            Color = SKColor.Parse("#2a3060").WithAlpha(40),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 0.8f * ui,
            IsAntialias = true
        };
        using var ringFont = new SKFont(SKTypeface.Default, 10f * ui);
        using var ringText = new SKPaint { Color = SKColor.Parse("#8b95c9"), IsAntialias = true };

        float maxRadiusM = (float)(_viewportMeters * 1.12);

        // Radial compass spokes every 45 degrees (rotated through the 3D camera).
        var (center, _) = Project3D(0, 0, 0, cx, cy, ppm);
        for (int k = 0; k < 8; k++)
        {
            var (edge, _) = Project3D(k * 45f, maxRadiusM, 0, cx, cy, ppm);
            canvas.DrawLine(center, edge, spokePaint);
        }

        for (int k = 0; k < 16; k++)
        {
            var (edge, _) = Project3D(k * 22.5f, maxRadiusM * 0.55f, 0, cx, cy, ppm);
            canvas.DrawLine(center, edge, minorGrid);
        }

        double step = NiceStep(_viewportMeters / 4.0);
        double maxD = _viewportMeters * 1.12;
        for (double d = step; d <= maxD; d += step)
        {
            // Atmospheric depth-fog: rings further from YOU fade toward the horizon.
            float depthT = (float)Math.Clamp(d / maxD, 0, 1);
            ringPaint.Color = SKColor.Parse("#00f5d4").WithAlpha((byte)(105 - depthT * 65));

            // Build the ring as a projected 3D circle so it tilts/rotates correctly.
            using var path = new SKPath();
            for (int i = 0; i <= 64; i++)
            {
                var (p, _) = Project3D(i * (360f / 64f), (float)d, 0, cx, cy, ppm);
                if (i == 0) path.MoveTo(p); else path.LineTo(p);
            }
            canvas.DrawPath(path, ringPaint);

            var (lbl, _) = Project3D(90f, (float)d, 0, cx, cy, ppm);
            string label = d < 1.0 ? $"{d * 100:F0}cm" : $"{d:0.##}m";
            ringText.Color = SKColor.Parse("#8b95c9").WithAlpha((byte)(220 - depthT * 120));
            canvas.DrawText(label, lbl.X + 3, lbl.Y - 3, SKTextAlign.Left, ringFont, ringText);
        }
    }

    /// <summary>Chooses a clean ring step (cm/m friendly) near the requested value.</summary>
    private static double NiceStep(double raw)
    {
        double[] steps = { 0.1, 0.2, 0.25, 0.5, 1, 2, 2.5, 5, 10, 15 };
        foreach (var s in steps)
            if (raw <= s) return s;
        return 15;
    }

    private void DrawYou(SKCanvas canvas, SKPoint you)
    {
        float ui = UiScale;
        using var glow = new SKPaint
        {
            Color = SKColor.Parse("#00f5d4").WithAlpha(90),
            IsAntialias = true,
            MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 12f)
        };
        using var dot = new SKPaint { Color = SKColor.Parse("#00f5d4"), IsAntialias = true };
        using var font = new SKFont(SKTypeface.Default, 13f * ui) { Embolden = true };
        using var text = new SKPaint { Color = SKColor.Parse("#00f5d4"), IsAntialias = true };

        canvas.DrawCircle(you.X, you.Y, 18f * ui, glow);
        canvas.DrawCircle(you.X, you.Y, 7f * ui, dot);
        canvas.DrawText("YOU", you.X + 12 * ui, you.Y - 10 * ui, SKTextAlign.Left, font, text);
    }

    private void DrawSignalLinks(SKCanvas canvas, float cx, float cy, float scale, SKPoint receptor)
    {
        float ui = UiScale;
        float ppm = scale / (float)_viewportMeters;

        var selected = GetVisibleSignals();
        if (selected.Count == 0)
        {
            using var font = new SKFont(SKTypeface.Default, 16f);
            using var paint = new SKPaint { Color = SKColor.Parse("#8b95c9"), IsAntialias = true };
            canvas.DrawText("Select Wi-Fi routers in Signal Monitor to draw live signal links.",
                cx, cy - scale * 0.75f, SKTextAlign.Center, font, paint);
            return;
        }

        bool dense = selected.Count > 8;
        float densityScale = ui / MathF.Sqrt(MathF.Max(1f, selected.Count / 6f));
        densityScale = Math.Clamp(densityScale, 0.45f, 2.2f);

        var labelBssids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrEmpty(_hoverBssid))
            labelBssids.Add(_hoverBssid);
        foreach (var s in selected.Where(n => n.IsSelected).Take(12))
            labelBssids.Add(s.Bssid);
        if (!dense || _viewportMeters < 5)
        {
            foreach (var s in selected.OrderBy(n => n.DistanceMeters).Take(_showDetailLabels ? 10 : 6))
                labelBssids.Add(s.Bssid);
        }

        using var linkPaint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeCap = SKStrokeCap.Round };

        var items = selected.Select(signal =>
        {
            var (wx, wz, trueDistance, bearingDeg, calibrated, offRange) = GetWorldPosition(signal);
            float strength = Math.Clamp((signal.LatestRssi + 100f) / 80f, 0.05f, 1f);
            float heightM = (0.12f + strength * 0.4f) * (float)_viewportMeters;

            var (floor, depth) = Project3DWorld(wx, wz, 0, cx, cy, ppm);
            var (top, _) = Project3DWorld(wx, wz, heightM, cx, cy, ppm);
            bool hovered = string.Equals(signal.Bssid, _hoverBssid, StringComparison.OrdinalIgnoreCase);
            bool showFull = hovered || (_showDetailLabels && labelBssids.Contains(signal.Bssid));
            return (signal, wx, wz, trueDistance, bearingDeg, calibrated, offRange, strength, floor, top, depth, showFull, hovered);
        })
        .OrderByDescending(t => t.depth)
        .ToList();

        foreach (var it in items)
        {
            var signal = it.signal;
            float strength = it.strength;
            SKColor color = SignalColor(signal.LatestRssi);
            var floor = it.floor;
            var nodeTop = it.top;
            float nodeRadius = (4f + strength * 6f) * densityScale;
            bool isHighlighted = it.hovered || signal.IsSelected;

            using var shadowPaint = new SKPaint
            {
                Color = color.WithAlpha(40),
                IsAntialias = true,
                MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 10f * densityScale)
            };
            using var stickPaint = new SKPaint
            {
                Color = color.WithAlpha(190),
                StrokeWidth = (2f + strength * 2f) * densityScale,
                IsAntialias = true
            };
            using var dotPaint = new SKPaint { Color = color, IsAntialias = true };

            linkPaint.Color = color.WithAlpha((byte)(90 + strength * 120));
            linkPaint.StrokeWidth = (1.5f + strength * 4f) * densityScale;
            canvas.DrawLine(floor, receptor, linkPaint);

            if (isHighlighted || !dense)
            {
                DrawSignalPulses(canvas, floor, receptor, color, strength, densityScale);
                if (signal.Variance > 0.3 || signal.MotionConfidence > 0.03)
                    DrawInterferenceStructures(canvas, floor, receptor, color, signal.Variance, signal.MotionConfidence,
                        signal.LatestRssi, it.trueDistance, densityScale);
            }

            if (signal.MotionConfidence > 0.05 || _motionConfidence > 0.3)
            {
                float pulse = (14f + (float)(signal.MotionConfidence * 20f)) * densityScale;
                canvas.DrawCircle(floor, pulse, shadowPaint);
            }

            if (it.offRange)
            {
                using var edgeMark = new SKPaint
                {
                    Color = SKColor.Parse("#f5a623").WithAlpha(200),
                    Style = SKPaintStyle.Stroke,
                    StrokeWidth = 2f * densityScale,
                    IsAntialias = true,
                    PathEffect = SKPathEffect.CreateDash(new[] { 4f * densityScale, 3f * densityScale }, 0)
                };
                canvas.DrawCircle(floor.X, floor.Y, nodeRadius + 5f * densityScale, edgeMark);
            }

            canvas.DrawLine(floor.X, floor.Y, nodeTop.X, nodeTop.Y, stickPaint);
            canvas.DrawCircle(nodeTop.X, nodeTop.Y, nodeRadius, shadowPaint);
            canvas.DrawCircle(nodeTop.X, nodeTop.Y, nodeRadius, dotPaint);

            if (isHighlighted)
            {
                using var ring = new SKPaint
                {
                    Color = SKColors.White.WithAlpha(180),
                    Style = SKPaintStyle.Stroke,
                    StrokeWidth = 1.5f * densityScale,
                    IsAntialias = true
                };
                canvas.DrawCircle(nodeTop.X, nodeTop.Y, nodeRadius + 3f * densityScale, ring);
            }

            string name = string.IsNullOrWhiteSpace(signal.Ssid)
                ? signal.Bssid[..Math.Min(8, signal.Bssid.Length)]
                : signal.Ssid;
            if (name.Length > 18) name = name[..17] + "…";

            if (it.showFull)
            {
                float plateW = 220f * densityScale;
                float plateH = 44f * densityScale;
                float baseX = nodeTop.X + 6 * densityScale;
                float baseY = nodeTop.Y - plateH - 4 * densityScale;
                float? placedTop = null;
                for (int attempt = 0; attempt < 5; attempt++)
                {
                    float tryTop = baseY - attempt * (plateH + 4f * densityScale);
                    var rect = SKRect.Create(baseX - 2f, tryTop - 2f, plateW + 4f, plateH + 4f);
                    bool overlap = false;
                    foreach (var r in _labelRects)
                        if (rect.Left < r.Right && rect.Right > r.Left && rect.Top < r.Bottom && rect.Bottom > r.Top) { overlap = true; break; }
                    if (!overlap) { _labelRects.Add(rect); placedTop = tryTop; break; }
                }

                if (placedTop is float plateTop)
                {
                    using var labelFont = new SKFont(SKTypeface.Default, 11f * densityScale) { Embolden = true };
                    using var labelPaint = new SKPaint { Color = SKColors.White.WithAlpha(220), IsAntialias = true };
                    using var smallFont = new SKFont(SKTypeface.Default, 9f * densityScale);
                    using var smallPaint = new SKPaint { Color = SKColor.Parse("#8b95c9"), IsAntialias = true };

                    string edgeTag = it.offRange ? " · edge" : "";
                    string posTag = it.calibrated ? " · calibrated" : " · estimated";
                    DrawLabelPlate(canvas, baseX, plateTop, plateW, plateH);
                    canvas.DrawText($"{name}{edgeTag}", baseX + 4 * densityScale, plateTop + 18 * densityScale, SKTextAlign.Left, labelFont, labelPaint);
                    canvas.DrawText($"{it.trueDistance:F1} m · {it.bearingDeg:F0}° · {signal.LatestRssi} dBm · Q {signal.SignalQuality}%{posTag}",
                        baseX + 4 * densityScale, plateTop + 32 * densityScale, SKTextAlign.Left, smallFont, smallPaint);
                    canvas.DrawText($"Var {signal.Variance:F1} · Motion {signal.MotionConfidence * 100:F0}% · Live",
                        baseX + 4 * densityScale, plateTop + 44 * densityScale, SKTextAlign.Left, smallFont, smallPaint);
                }
            }
            else if (dense)
            {
                using var miniFont = new SKFont(SKTypeface.Default, 8f * densityScale);
                using var miniPaint = new SKPaint { Color = color.WithAlpha(200), IsAntialias = true };
                if (TryPlaceLabel(miniFont, name, nodeTop.X + nodeRadius + 2, nodeTop.Y + 3, SKTextAlign.Left, out var minipos))
                    canvas.DrawText(name, minipos.X, minipos.Y, SKTextAlign.Left, miniFont, miniPaint);
            }
        }
    }

    private void DrawSignalPulses(SKCanvas canvas, SKPoint start, SKPoint end, SKColor color, float strength, float scale)
    {
        using var pulsePaint = new SKPaint
        {
            Color = SKColors.White.WithAlpha((byte)(80 + strength * 140)),
            IsAntialias = true,
            MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 3f)
        };

        for (int i = 0; i < 5; i++)
        {
            float t = (_signalPhase + i / 5f) % 1f;
            float x = start.X + (end.X - start.X) * t;
            float y = start.Y + (end.Y - start.Y) * t;
            canvas.DrawCircle(x, y, (2f + strength * 2f) * scale, pulsePaint);
        }
    }

    private static void DrawInterferenceStructures(SKCanvas canvas, SKPoint start, SKPoint end, SKColor color, double variance, double motionConfidence, int rssiDbm, float sourceDistanceMeters, float ui)
    {
        double intensity = Math.Clamp(Math.Max(motionConfidence, variance / 8.0), 0.0, 1.0);
        if (intensity < 0.02)
            return;

        const double txPower = -40.0;
        const double n = 2.7;
        double expectedRssi = txPower - 10 * n * Math.Log10(Math.Max(0.05, sourceDistanceMeters));
        double excessLoss = Math.Clamp(expectedRssi - rssiDbm, 0, 40);
        float t = (float)Math.Clamp(0.1 + excessLoss / 50.0 + variance / 20.0, 0.08, 0.88);

        using var wallFill = new SKPaint
        {
            Color = color.WithAlpha((byte)(35 + intensity * 120)),
            Style = SKPaintStyle.Fill,
            IsAntialias = true,
        };
        using var wallStroke = new SKPaint
        {
            Color = color.WithAlpha((byte)(120 + intensity * 100)),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = (1.2f + (float)(intensity * 2f)) * ui,
            IsAntialias = true
        };
        using var hotPaint = new SKPaint
        {
            Color = color.WithAlpha((byte)(45 + intensity * 105)),
            IsAntialias = true,
            MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, (float)(8 + intensity * 14) * ui)
        };
        using var font = new SKFont(SKTypeface.Default, 9f * ui) { Embolden = true };
        using var text = new SKPaint { Color = SKColors.White.WithAlpha(210), IsAntialias = true };

        float dx = end.X - start.X;
        float dy = end.Y - start.Y;
        float len = MathF.Max(1f, MathF.Sqrt(dx * dx + dy * dy));
        float nx = -dy / len;
        float ny = dx / len;

        float obstacleDistance = sourceDistanceMeters * t;
        float cx = start.X + dx * t;
        float cy = start.Y + dy * t;
        float width = (float)(12 + intensity * 36 + excessLoss * 0.4) * ui;
        float height = (float)(24 + intensity * 55 + excessLoss * 0.6) * ui;

        var p1 = new SKPoint(cx - nx * width, cy - ny * width);
        var p2 = new SKPoint(cx + nx * width, cy + ny * width);
        var p3 = new SKPoint(p2.X, p2.Y - height);
        var p4 = new SKPoint(p1.X, p1.Y - height);

        using var wall = new SKPath();
        wall.MoveTo(p1);
        wall.LineTo(p2);
        wall.LineTo(p3);
        wall.LineTo(p4);
        wall.Close();

        canvas.DrawCircle(cx, cy - height * 0.45f, width * 0.85f, hotPaint);
        canvas.DrawPath(wall, wallFill);
        canvas.DrawPath(wall, wallStroke);
        canvas.DrawText($"Atten {excessLoss:F0}dB @ {obstacleDistance:F1}m", cx, cy - height - 8, SKTextAlign.Center, font, text);
    }

    private static void DrawCompass(SKCanvas canvas, float cx, float cy, float scale)
    {
        float x = cx - scale * 1.1f;
        float y = cy - scale * 0.95f;
        float r = 34f;

        using var ring = new SKPaint
        {
            Color = SKColor.Parse("#2a3060").WithAlpha(220),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 2f,
            IsAntialias = true
        };
        using var tick = new SKPaint { Color = SKColor.Parse("#8b95c9"), StrokeWidth = 1.4f, IsAntialias = true };
        using var north = new SKPaint { Color = SKColor.Parse("#00f5d4"), StrokeWidth = 2.8f, IsAntialias = true };
        using var label = new SKPaint { Color = SKColors.White.WithAlpha(210), IsAntialias = true };
        using var font = new SKFont(SKTypeface.Default, 11f) { Embolden = true };

        canvas.DrawCircle(x, y, r, ring);
        canvas.DrawLine(x, y - r + 4, x, y + r - 4, tick);
        canvas.DrawLine(x - r + 4, y, x + r - 4, y, tick);
        canvas.DrawLine(x, y, x, y - r + 2, north);

        canvas.DrawText("N", x, y - r - 4, SKTextAlign.Center, font, label);
        canvas.DrawText("S", x, y + r + 14, SKTextAlign.Center, font, label);
        canvas.DrawText("E", x + r + 12, y + 4, SKTextAlign.Center, font, label);
        canvas.DrawText("W", x - r - 12, y + 4, SKTextAlign.Center, font, label);
    }

    private static void DrawLabelPlate(SKCanvas canvas, float x, float y, float w, float h)
    {
        using var fill = new SKPaint { Color = SKColor.Parse("#050816").WithAlpha(150), IsAntialias = true };
        using var stroke = new SKPaint
        {
            Color = SKColor.Parse("#2a3060").WithAlpha(200),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1,
            IsAntialias = true
        };
        var rect = new SKRoundRect(new SKRect(x, y, x + w, y + h), 5, 5);
        canvas.DrawRoundRect(rect, fill);
        canvas.DrawRoundRect(rect, stroke);
    }

    private void DrawLegend(SKCanvas canvas, float w, float h)
    {
        using var font = new SKFont(SKTypeface.Default, 11f);
        using var paint = new SKPaint { Color = SKColor.Parse("#8b95c9"), IsAntialias = true };
        string state = _classification.ToString().Replace("_", " ");
        string frame = _viewModel?.TimeFrameText ?? "Live @ 50ms";
        double mLatency = _viewModel?.MeasurementLatencyMs ?? 0;
        double eLatency = _viewModel?.EnvironmentLatencyMs ?? 0;
        string posSource = _viewModel?.PositionSource ?? "Polar estimate";
        int apShown = GetVisibleSignals().Count;
        string viewTag = _motionOnlyMode
            ? $" · {_zones.Count} motion zones"
            : $" · {apShown} APs";
        canvas.DrawText($"Range {_viewportMeters:F1}m{viewTag} · {posSource} · {frame} · M:{mLatency:F0}ms E:{eLatency:F0}ms · Motion {(_motionConfidence * 100):F0}% · LSTM {(_viewModel?.LstmMotionPct ?? 0):F0}% · CNN {(_viewModel?.CnnActivityPct ?? 0):F0}% · {state}",
            20, h - 22, SKTextAlign.Left, font, paint);
    }

    private void OnMapMouseWheel(object sender, MouseWheelEventArgs e)
    {
        ApplyZoom(e.Delta > 0 ? 0.82 : 1.22, e.GetPosition(MapCanvas));
        e.Handled = true;
    }

    private void OnZoomInClicked(object sender, System.Windows.RoutedEventArgs e) =>
        ApplyZoom(0.72, new System.Windows.Point(MapCanvas.ActualWidth / 2, MapCanvas.ActualHeight / 2));

    private void OnZoomOutClicked(object sender, System.Windows.RoutedEventArgs e) =>
        ApplyZoom(1.38, new System.Windows.Point(MapCanvas.ActualWidth / 2, MapCanvas.ActualHeight / 2));

    private void OnResetZoomClicked(object sender, System.Windows.RoutedEventArgs e)
    {
        _viewportMeters = DefaultViewportMeters;
        _yaw = 0f;
        _elev = 0.62f;
        _panPx = 0;
        _panPy = 0;
        _userAdjustedView = false;
        _autoFitPending = true;
        MapCanvas.InvalidateVisual();
    }

    private void OnFitClicked(object sender, System.Windows.RoutedEventArgs e)
    {
        _userAdjustedView = false;
        _autoFitPending = true;
        MapCanvas.InvalidateVisual();
    }

    private void OnToggleLabelsClicked(object sender, System.Windows.RoutedEventArgs e)
    {
        _showDetailLabels = !_showDetailLabels;
        if (LabelsToggleBtn != null)
        {
            LabelsToggleBtn.Content = _showDetailLabels ? "Labels: ON" : "Labels: OFF";
            LabelsToggleBtn.Foreground = _showDetailLabels
                ? (System.Windows.Media.Brush)FindResource("NeonCyanBrush")
                : (System.Windows.Media.Brush)FindResource("TextSecondaryBrush");
        }
        MapCanvas.InvalidateVisual();
    }

    private void OnLiveModeClicked(object sender, System.Windows.RoutedEventArgs e)
    {
        if (_viewModel == null) return;
        _viewModel.TimeFrameSeconds = 0;
        _viewModel.SampleIntervalMs = 50;
        MapCanvas.InvalidateVisual();
    }

    private void OnRate100Clicked(object sender, System.Windows.RoutedEventArgs e) => SetSampleInterval(100);

    private void OnRate50Clicked(object sender, System.Windows.RoutedEventArgs e) => SetSampleInterval(50);

    private void OnToggleMotionModeClicked(object sender, System.Windows.RoutedEventArgs e)
    {
        _motionOnlyMode = !_motionOnlyMode;
        _autoFitPending = true;
        _hoverBssid = null;
        _hoverZoneId = null;
        if (MotionModeToggleBtn != null)
        {
            MotionModeToggleBtn.Content = _motionOnlyMode ? "Motion only" : "3D + APs";
            MotionModeToggleBtn.Foreground = _motionOnlyMode
                ? (System.Windows.Media.Brush)FindResource("WarningAmberBrush")
                : (System.Windows.Media.Brush)FindResource("NeonCyanBrush");
        }
        MapCanvas.InvalidateVisual();
    }

    private void OnSourceSelectedClicked(object sender, System.Windows.RoutedEventArgs e) => SetSourceMode(SignalSourceMode.Selected);

    private void OnSourceAllClicked(object sender, System.Windows.RoutedEventArgs e) => SetSourceMode(SignalSourceMode.All);

    private void OnSourceConnectedClicked(object sender, System.Windows.RoutedEventArgs e) => SetSourceMode(SignalSourceMode.Connected);

    private void SetSampleInterval(int ms)
    {
        if (_viewModel == null)
            return;

        _viewModel.TimeFrameSeconds = 0;
        _viewModel.SampleIntervalMs = ms;
        MapCanvas.InvalidateVisual();
    }

    private void SetSourceMode(SignalSourceMode mode)
    {
        if (_viewModel == null)
            return;

        _viewModel.SelectedSignalSource = mode;
        _userAdjustedView = false;
        _autoFitPending = true;
        MapCanvas.InvalidateVisual();
    }

    private void ApplyZoom(double factor, System.Windows.Point anchor)
    {
        double oldV = _viewportMeters;
        double newV = Math.Clamp(oldV * factor, MinViewportMeters, MaxViewportMeters);
        if (Math.Abs(newV - oldV) < 0.0001) return;

        float cx = (float)MapCanvas.ActualWidth / 2f;
        float cy = (float)MapCanvas.ActualHeight / 2f;
        float ratio = (float)(newV / oldV);

        float ax = (float)anchor.X - cx;
        float ay = (float)anchor.Y - cy;
        _panPx = ax - (ax - _panPx) * ratio;
        _panPy = ay - (ay - _panPy) * ratio;

        _viewportMeters = newV;
        _userAdjustedView = true;
        MapCanvas.InvalidateVisual();
    }

    private static SKColor SignalColor(int rssi) =>
        rssi > -50 ? SKColor.Parse("#00f5d4") :
        rssi > -70 ? SKColor.Parse("#f59e0b") :
                     SKColor.Parse("#ef4444");

    private static double EstimateDistanceMeters(int rssiDbm)
    {
        const double txPower = -40.0;
        const double n = 2.7;
        return Math.Pow(10.0, (txPower - rssiDbm) / (10.0 * n));
    }

    private sealed class MapSignal(string bssid, string ssid)
    {
        public string Bssid { get; } = bssid;
        public string Ssid { get; set; } = string.IsNullOrWhiteSpace(ssid) ? "Hidden" : ssid;
        public int Rssi { get; set; } = -100;
        public int SignalQuality { get; set; }
        public int Channel { get; set; }
        public double DistanceMeters { get; set; } = 50;
        public double MotionConfidence { get; set; }
        public double Variance { get; set; }
    }
}
