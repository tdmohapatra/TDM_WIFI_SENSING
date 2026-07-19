using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using ScottPlot.Plottables;
using SkiaSharp;
using StarSensing.Core.Protos;
using StarSensing.Dashboard.Models;
using StarSensing.Dashboard.Services;
using StarSensing.Dashboard.ViewModels;

namespace StarSensing.Dashboard.Views;

public partial class HeatmapView : UserControl
{
    private HeatmapViewModel? _viewModel;
    private readonly double[,] _channelGrid;
    private readonly double[,] _floorGrid;
    private Heatmap? _channelHeatmap;
    private const int Channels = 14;

    // 3D spatial camera
    private double _viewportMeters = HeatmapViewModel.DefaultViewportMeters;
    private float _yaw;
    private float _elev = 0.62f;
    private float _panPx;
    private float _panPy;
    private bool _orbiting;
    private bool _panning;
    private Point _lastDrag;
    private float _animPhase;
    private readonly List<SKRect> _labelRects = new();
    private float _probePhase;
    private bool _probeActive;
    private bool _spatialFullscreen;
    private string? _probeZoneId;
    private double _probeTargetDistM;
    private readonly DispatcherTimer _spatialTimer;

    private float UiScale => Math.Clamp((float)(HeatmapViewModel.DefaultViewportMeters / _viewportMeters), 0.55f, 2.8f);

    public HeatmapView()
    {
        InitializeComponent();
        _channelGrid = new double[Channels, HeatmapViewModel.TimeSteps];
        _floorGrid = new double[HeatmapViewModel.FloorSize, HeatmapViewModel.FloorSize];

        for (int r = 0; r < Channels; r++)
            for (int c = 0; c < HeatmapViewModel.TimeSteps; c++)
                _channelGrid[r, c] = -100;

        _channelHeatmap = ChannelPlot.Plot.Add.Heatmap(_channelGrid);
        _channelHeatmap.Colormap = new ScottPlot.Colormaps.Turbo();
        _channelHeatmap.ManualRange = new ScottPlot.Range(-100, -20);
        _channelHeatmap.FlipRows = true;
        ChannelPlot.Plot.Add.ColorBar(_channelHeatmap);
        ChannelPlot.Plot.Title("2.4 GHz Channel Waterfall (RSSI dBm)");
        ChannelPlot.Plot.XLabel("Time →");
        ChannelPlot.Plot.YLabel("Wi-Fi Channel");
        ChannelPlot.Plot.Axes.Left.SetTicks(
            Enumerable.Range(0, Channels).Select(i => (double)i).ToArray(),
            Enumerable.Range(1, Channels).Select(i => $"Ch {i}").ToArray());
        ChannelPlot.Refresh();

        _spatialTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _spatialTimer.Tick += (_, _) =>
        {
            _animPhase = (_animPhase + 0.022f) % 1f;
            _viewModel?.TickMotionTrails(0.016f);
            if (_probeActive)
            {
                _probePhase += 0.018f;
                if (_probePhase >= 1f)
                {
                    _probeActive = false;
                    if (_viewModel != null)
                        _viewModel.ProbeStatus = _probeTargetDistM < 1.0
                            ? $"Echo @ {_probeTargetDistM * 100:F0} cm — motion zone locked"
                            : $"Echo @ {_probeTargetDistM:F2} m — motion zone locked";
                }
            }

            if (_viewModel?.SpatialMode == true)
                SpatialCanvas.InvalidateVisual();
        };
        _spatialTimer.Start();

        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_viewModel != null)
        {
            _viewModel.OnDataReceived -= ViewModel_OnDataReceived;
            _viewModel.OnSpatialFrameReady -= ViewModel_OnSpatialFrame;
            _viewModel.PropertyChanged -= ViewModel_PropertyChanged;
            _viewModel.BlockZonesUpdated -= ViewModel_BlockZonesUpdated;
            _viewModel.ProbeFired -= ViewModel_ProbeFired;
            _viewModel.SpatialMemoryUpdated -= ViewModel_SpatialMemoryUpdated;
        }

        _viewModel = e.NewValue as HeatmapViewModel;
        if (_viewModel == null) return;

        _viewModel.OnDataReceived += ViewModel_OnDataReceived;
        _viewModel.OnSpatialFrameReady += ViewModel_OnSpatialFrame;
        _viewModel.PropertyChanged += ViewModel_PropertyChanged;
        _viewModel.BlockZonesUpdated += ViewModel_BlockZonesUpdated;
        _viewModel.ProbeFired += ViewModel_ProbeFired;
        _viewModel.SpatialMemoryUpdated += ViewModel_SpatialMemoryUpdated;
        _viewportMeters = _viewModel.ViewportMeters;
        _ = _viewModel.LoadHistoryAsync(InjectChannelRow);
        ApplyModeVisibility();
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(HeatmapViewModel.SpatialMode) or nameof(HeatmapViewModel.SelectedZoneId)
            or nameof(HeatmapViewModel.ViewportMeters))
        {
            if (e.PropertyName == nameof(HeatmapViewModel.ViewportMeters) && _viewModel != null)
                _viewportMeters = _viewModel.ViewportMeters;
            Dispatcher.Invoke(() =>
            {
                ApplyModeVisibility();
                SpatialCanvas.InvalidateVisual();
            });
        }
    }

    private void ViewModel_ProbeFired()
    {
        Dispatcher.Invoke(() =>
        {
            if (_viewModel == null) return;
            _probeZoneId = _viewModel.ProbingZoneId;
            _probeTargetDistM = _viewModel.ProbeDistanceMeters;
            _probePhase = 0f;
            _probeActive = true;
            SpatialCanvas.InvalidateVisual();
        });
    }

    private void ViewModel_SpatialMemoryUpdated() =>
        Dispatcher.Invoke(() => SpatialCanvas.InvalidateVisual());

    private void ViewModel_BlockZonesUpdated() =>
        Dispatcher.Invoke(() => SpatialCanvas.InvalidateVisual());

    private void ApplyModeVisibility()
    {
        bool spatial = _viewModel?.SpatialMode ?? false;
        ChannelPlot.Visibility = spatial ? Visibility.Collapsed : Visibility.Visible;
        SpatialPanel.Visibility = spatial ? Visibility.Visible : Visibility.Collapsed;
        bool showDetail = spatial && !_spatialFullscreen;
        ApListPanel.Visibility = showDetail ? Visibility.Visible : Visibility.Collapsed;
        DetailColumn.Width = showDetail ? new GridLength(250) : new GridLength(0);
    }

    /// <summary>Focus mode — collapses the detail side panel so the 3D canvas fills the tab. Toggle stays in the header (always reachable).</summary>
    private void OnToggleFullscreen(object sender, RoutedEventArgs e)
    {
        _spatialFullscreen = !_spatialFullscreen;
        FullscreenButton.Content = _spatialFullscreen ? "⛶ Exit Fullscreen" : "⛶ Fullscreen";
        ApplyModeVisibility();
        SpatialCanvas.InvalidateVisual();
    }

    private void OnChannelMode(object sender, RoutedEventArgs e)
    {
        if (_viewModel != null) _viewModel.SpatialMode = false;
    }

    private void OnSpatialMode(object sender, RoutedEventArgs e)
    {
        if (_viewModel == null) return;
        _viewModel.SpatialMode = true;
        _viewModel.SetSpatialFastStreamCommand.Execute(null);
    }

    private void OnZoomIn(object sender, RoutedEventArgs e)
    {
        SetViewport(_viewportMeters * 0.78);
    }

    private void OnZoomOut(object sender, RoutedEventArgs e)
    {
        SetViewport(_viewportMeters * 1.28);
    }

    private void OnZoomReset(object sender, RoutedEventArgs e)
    {
        _yaw = 0;
        _elev = 0.62f;
        _panPx = 0;
        _panPy = 0;
        SetViewport(HeatmapViewModel.DefaultViewportMeters);
    }

    private void SetViewport(double meters)
    {
        _viewportMeters = Math.Clamp(meters, HeatmapViewModel.MinViewportMeters, HeatmapViewModel.MaxViewportMeters);
        if (_viewModel != null)
            _viewModel.ViewportMeters = _viewportMeters;
        SpatialCanvas.InvalidateVisual();
    }

    private void OnSpatialMouseWheel(object sender, MouseWheelEventArgs e)
    {
        double factor = e.Delta > 0 ? 0.88 : 1.14;
        SetViewport(_viewportMeters * factor);
        e.Handled = true;
    }

    private void OnSpatialMouseDown(object sender, MouseButtonEventArgs e)
    {
        _lastDrag = e.GetPosition(SpatialCanvas);
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            _orbiting = true;
            SpatialCanvas.CaptureMouse();
        }
        else if (e.RightButton == MouseButtonState.Pressed || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            _panning = true;
            SpatialCanvas.CaptureMouse();
        }
        else if (e.ChangedButton == MouseButton.Left && e.ClickCount >= 2)
        {
            OnZoomReset(sender, e);
        }
    }

    private void OnSpatialMouseMove(object sender, MouseEventArgs e)
    {
        var p = e.GetPosition(SpatialCanvas);
        if (_orbiting)
        {
            _yaw += (float)(p.X - _lastDrag.X) * 0.01f;
            _elev = Math.Clamp(_elev + (float)(p.Y - _lastDrag.Y) * 0.01f, 0.12f, 1.55f);
            _lastDrag = p;
            SpatialCanvas.InvalidateVisual();
            return;
        }

        if (_panning)
        {
            _panPx += (float)(p.X - _lastDrag.X);
            _panPy += (float)(p.Y - _lastDrag.Y);
            _lastDrag = p;
            SpatialCanvas.InvalidateVisual();
        }
    }

    private void OnSpatialMouseUp(object sender, MouseButtonEventArgs e)
    {
        _orbiting = false;
        _panning = false;
        SpatialCanvas.ReleaseMouseCapture();
    }

    private void InjectChannelRow(int channel, double rssi)
    {
        if (channel < 1 || channel > 14) return;
        ShiftAndSet(_channelGrid, channel - 1, rssi);
        _channelHeatmap?.Update();
        ChannelPlot.Refresh();
    }

    private void ViewModel_OnDataReceived(MeasurementBatch batch)
    {
        Dispatcher.Invoke(() =>
        {
            for (int r = 0; r < Channels; r++)
                for (int c = 0; c < HeatmapViewModel.TimeSteps - 1; c++)
                    _channelGrid[r, c] = _channelGrid[r, c + 1];
            for (int r = 0; r < Channels; r++)
                _channelGrid[r, HeatmapViewModel.TimeSteps - 1] = -100;

            foreach (var m in batch.Measurements)
            {
                if (m.Channel is >= 1 and <= 14)
                {
                    int row = m.Channel - 1;
                    if (m.RssiDbm > _channelGrid[row, HeatmapViewModel.TimeSteps - 1])
                        _channelGrid[row, HeatmapViewModel.TimeSteps - 1] = m.RssiDbm;
                }
            }
            _channelHeatmap?.Update();
            ChannelPlot.Refresh();
        });
    }

    private void ViewModel_OnSpatialFrame(double[,] frame)
    {
        Dispatcher.Invoke(() =>
        {
            int n = Math.Min(HeatmapViewModel.FloorSize, frame.GetLength(0));
            bool live = _viewModel?.TimeRange.IsLiveMode ?? true;
            for (int r = 0; r < n; r++)
            for (int c = 0; c < n; c++)
                _floorGrid[r, c] = live
                    ? _floorGrid[r, c] * 0.75 + frame[r, c] * 0.25
                    : frame[r, c];
            SpatialCanvas.InvalidateVisual();
        });
    }

    private static void ShiftAndSet(double[,] grid, int row, double value)
    {
        int cols = grid.GetLength(1);
        for (int c = 0; c < cols - 1; c++)
            grid[row, c] = grid[row, c + 1];
        grid[row, cols - 1] = value;
    }

    private void OnPaintSpatial(object sender, SkiaSharp.Views.Desktop.SKPaintSurfaceEventArgs e)
    {
        var canvas = e.Surface.Canvas;
        float w = e.Info.Width;
        float h = e.Info.Height;
        canvas.Clear(SKColor.Parse("#050818"));
        _labelRects.Clear();

        float cx = w / 2f;
        float cy = h / 2f;
        float scale = Math.Min(w, h) * 0.44f;
        float ppm = scale / (float)_viewportMeters;
        float ui = UiScale;

        var (you, _) = Project3DWorld(0, 0, 0, cx, cy, ppm);

        DrawGround3D(canvas, cx, cy, scale, ppm, ui);
        DrawScanPulse3D(canvas, cx, cy, ppm, ui);
        DrawStoredMemoryLayer(canvas, cx, cy, ppm, ui);
        DrawAnimatedHeatSurface(canvas, cx, cy, ppm, ui);
        DrawMotionTrails3D(canvas, cx, cy, ppm, ui);
        DrawCommonHotspots3D(canvas, cx, cy, ppm, ui);
        DrawBlockZones3D(canvas, cx, cy, ppm, ui);
        DrawSignalProbes(canvas, cx, cy, ppm, ui, you);
        DrawYou3D(canvas, you, ui);
        DrawCompass3D(canvas, cx, cy, scale, ppm, ui);
        DrawLegend3D(canvas, w, h);
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

    /// <summary>Greedy collision-avoided label placement — stacks downward in fixed steps, skips draw if no slot found, so dense zones never garble into overlapping text.</summary>
    private bool TryPlaceLabel(SKFont font, string txt, float anchorX, float anchorY, SKTextAlign align, out SKPoint pos)
    {
        float width = font.MeasureText(txt);
        float lineH = font.Size + 4f;
        for (int attempt = 0; attempt < 6; attempt++)
        {
            float oy = anchorY + attempt * lineH;
            float left = align switch
            {
                SKTextAlign.Center => anchorX - width / 2f,
                SKTextAlign.Right => anchorX - width,
                _ => anchorX
            };
            var rect = SKRect.Create(left - 2f, oy - lineH, width + 4f, lineH);
            bool overlap = false;
            foreach (var r in _labelRects)
            {
                if (rect.Left < r.Right && rect.Right > r.Left && rect.Top < r.Bottom && rect.Bottom > r.Top)
                {
                    overlap = true;
                    break;
                }
            }
            if (!overlap)
            {
                _labelRects.Add(rect);
                pos = new SKPoint(anchorX, oy);
                return true;
            }
        }
        pos = default;
        return false;
    }

    private (float east, float north) NormToWorld(double nx, double ny)
    {
        float span = (float)(_viewportMeters * 2.0);
        return ((float)((nx - 0.5) * span), (float)((ny - 0.5) * span));
    }

    private void DrawGround3D(SKCanvas canvas, float cx, float cy, float scale, float ppm, float ui)
    {
        using var font = new SKFont(SKTypeface.Default, 9f * ui);
        using var label = new SKPaint { Color = SKColor.Parse("#6a7599"), IsAntialias = true };

        var (center, _) = Project3DWorld(0, 0, 0, cx, cy, ppm);
        double maxD = _viewportMeters * 1.05;
        double step = NiceDistanceStep(_viewportMeters);

        // Depth cue: rings fade toward the horizon (atmospheric perspective) so the floor reads as a 3D plane, not flat wallpaper.
        for (double d = step; d <= maxD; d += step)
        {
            float depthT = (float)Math.Clamp(d / maxD, 0, 1);
            using var ringPaint = new SKPaint
            {
                Color = SKColor.Parse("#4361ee").WithAlpha((byte)(115 - depthT * 75)),
                Style = SKPaintStyle.Stroke,
                StrokeWidth = (1.5f - depthT * 0.6f) * ui,
                IsAntialias = true
            };
            using var path = new SKPath();
            for (int i = 0; i <= 64; i++)
            {
                float a = i * MathF.PI * 2f / 64f;
                float ex = (float)(d * Math.Sin(a));
                float nz = (float)(d * Math.Cos(a));
                var (p, _) = Project3DWorld(ex, nz, 0, cx, cy, ppm);
                if (i == 0) path.MoveTo(p); else path.LineTo(p);
            }
            canvas.DrawPath(path, ringPaint);
            var (lbl, _) = Project3DWorld(0, (float)d, 0, cx, cy, ppm);
            string txt = d < 1.0 ? $"{d * 100:F0}cm" : $"{d:0.##}m";
            canvas.DrawText(txt, lbl.X + 3, lbl.Y - 2, font, label);
        }

        for (int k = 0; k < 8; k++)
        {
            float depthT = (float)Math.Abs(Math.Cos(k * 45.0 * Math.PI / 180.0 - _yaw)) * 0.4f;
            using var spokePaint = new SKPaint
            {
                Color = SKColor.Parse("#2a3060").WithAlpha((byte)(125 - depthT * 80)),
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 1f * ui,
                IsAntialias = true
            };
            var (edge, _) = Project3D(k * 45f, (float)_viewportMeters, 0, cx, cy, ppm);
            canvas.DrawLine(center, edge, spokePaint);
        }
    }

    /// <summary>Expanding cyan rings radiating from YOU — visualizes the live 50ms sensing pulse as a signal wave sweeping the floor.</summary>
    private void DrawScanPulse3D(SKCanvas canvas, float cx, float cy, float ppm, float ui)
    {
        for (int i = 0; i < 2; i++)
        {
            float phase = (_animPhase * 0.5f + i * 0.5f) % 1f;
            float radius = phase * (float)_viewportMeters * 0.95f;
            if (radius < 0.04f) continue;

            using var wave = new SKPaint
            {
                Color = SKColor.Parse("#00f5d4").WithAlpha((byte)((1f - phase) * 65f)),
                Style = SKPaintStyle.Stroke,
                StrokeWidth = (1.4f - phase * 0.6f) * ui,
                IsAntialias = true
            };
            using var path = new SKPath();
            for (int k = 0; k <= 64; k++)
            {
                float a = k * MathF.PI * 2f / 64f;
                var (p, _) = Project3DWorld(radius * MathF.Sin(a), radius * MathF.Cos(a), 0.01f * (float)_viewportMeters, cx, cy, ppm);
                if (k == 0) path.MoveTo(p); else path.LineTo(p);
            }
            path.Close();
            canvas.DrawPath(path, wave);
        }
    }

    /// <summary>Compact twin confidence bars (motion=amber, occupancy=cyan) — gives a "bars" readout of detection accuracy alongside each live zone label.</summary>
    private static void DrawConfidenceBars(SKCanvas canvas, float x, float y, double motionPct, double occPct, float ui)
    {
        const float barW = 34f, barH = 2.6f, gap = 4.2f;
        using var track = new SKPaint { Color = SKColor.Parse("#1a2050").WithAlpha(200), IsAntialias = true };
        using var motionFill = new SKPaint { Color = SKColor.Parse("#f59e0b").WithAlpha(230), IsAntialias = true };
        using var occFill = new SKPaint { Color = SKColor.Parse("#22d3ee").WithAlpha(230), IsAntialias = true };

        float w = barW * ui, h = barH * ui;
        canvas.DrawRect(SKRect.Create(x, y, w, h), track);
        canvas.DrawRect(SKRect.Create(x, y, w * (float)Math.Clamp(motionPct / 100.0, 0, 1), h), motionFill);
        canvas.DrawRect(SKRect.Create(x, y + gap * ui, w, h), track);
        canvas.DrawRect(SKRect.Create(x, y + gap * ui, w * (float)Math.Clamp(occPct / 100.0, 0, 1), h), occFill);
    }

    private (SKPoint Pt, float Depth) Project3D(float bearingDeg, float radiusM, float heightM, float cx, float cy, float ppm)
    {
        var (east, north) = BearingStoreService.PolarToMeters(bearingDeg, radiusM);
        return Project3DWorld((float)east, (float)north, heightM, cx, cy, ppm);
    }

    private static double NiceDistanceStep(double viewportM)
    {
        if (viewportM <= 0.5) return 0.1;
        if (viewportM <= 2) return 0.25;
        if (viewportM <= 8) return 1.0;
        if (viewportM <= 20) return 2.5;
        return 5.0;
    }

    private void DrawAnimatedHeatSurface(SKCanvas canvas, float cx, float cy, float ppm, float ui)
    {
        int cells = 20;
        double maxVal = 0.02;
        for (int r = 0; r < cells; r++)
        for (int c = 0; c < cells; c++)
        {
            float u = (c + 0.5f) / cells;
            float v = (r + 0.5f) / cells;
            maxVal = Math.Max(maxVal, SampleGridBilinear(u, v));
        }

        if (maxVal < 0.03) return;

        float span = (float)(_viewportMeters * 2.0);
        float cellW = span / cells;
        var quads = new List<(SKPoint[] pts, float t, float pulse)>();

        for (int r = 0; r < cells; r++)
        {
            for (int c = 0; c < cells; c++)
            {
                float u = (c + 0.5f) / cells;
                float v = (r + 0.5f) / cells;
                double val = SampleGridBilinear(u, v);
                if (val < maxVal * 0.08) continue;

                float t = (float)(val / maxVal);
                float pulse = 0.65f + 0.35f * MathF.Sin(_animPhase * MathF.PI * 2f + (c + r) * 0.4f);
                float east0 = -span / 2f + c * cellW;
                float north0 = -span / 2f + r * cellW;
                float h = (0.02f + t * 0.12f * pulse) * (float)_viewportMeters;

                var p0 = Project3DWorld(east0, north0, h, cx, cy, ppm).Pt;
                var p1 = Project3DWorld(east0 + cellW, north0, h, cx, cy, ppm).Pt;
                var p2 = Project3DWorld(east0 + cellW, north0 + cellW, h, cx, cy, ppm).Pt;
                var p3 = Project3DWorld(east0, north0 + cellW, h, cx, cy, ppm).Pt;
                quads.Add((new[] { p0, p1, p2, p3 }, t, pulse));
            }
        }

        foreach (var (pts, t, pulse) in quads.OrderBy(q => q.pts.Average(p => p.Y)))
        {
            using var paint = new SKPaint
            {
                Color = ActivityColor(t).WithAlpha((byte)Math.Clamp(40 + t * pulse * 160, 35, 210)),
                IsAntialias = true,
                Style = SKPaintStyle.Fill
            };
            using var path = new SKPath();
            path.MoveTo(pts[0]);
            path.LineTo(pts[1]);
            path.LineTo(pts[2]);
            path.LineTo(pts[3]);
            path.Close();
            canvas.DrawPath(path, paint);
        }
    }

    private void DrawStoredMemoryLayer(SKCanvas canvas, float cx, float cy, float ppm, float ui)
    {
        if (_viewModel == null) return;
        var mem = _viewModel.SpatialMemory;
        int step = 4;
        for (int r = 0; r < SpatialMemoryStore.GridSize; r += step)
        {
            for (int c = 0; c < SpatialMemoryStore.GridSize; c += step)
            {
                double motion = mem.MotionMemory[r, c];
                double obs = mem.ObstacleMemory[r, c];
                if (motion < 0.04 && obs < 0.04) continue;

                float nx = c / (float)(SpatialMemoryStore.GridSize - 1);
                float ny = r / (float)(SpatialMemoryStore.GridSize - 1);
                var (east, north) = NormToWorld(nx, ny);
                float h = (float)((motion * 0.08 + obs * 0.05) * _viewportMeters);
                var (pt, _) = Project3DWorld(east, north, h, cx, cy, ppm);

                if (motion >= obs && motion > 0.04)
                {
                    using var p = new SKPaint
                    {
                        Color = SKColor.Parse("#3b82f6").WithAlpha((byte)Math.Clamp(motion * 65, 18, 80)),
                        IsAntialias = true,
                        MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 9f * ui)
                    };
                    canvas.DrawCircle(pt, (4f + (float)motion * 10f) * ui, p);
                }
                else if (obs > 0.04)
                {
                    using var p = new SKPaint
                    {
                        Color = SKColor.Parse("#64748b").WithAlpha((byte)Math.Clamp(obs * 75, 18, 95)),
                        IsAntialias = true,
                        MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 8f * ui)
                    };
                    canvas.DrawCircle(pt, (3f + (float)obs * 8f) * ui, p);
                }
            }
        }
    }

    private void DrawMotionTrails3D(SKCanvas canvas, float cx, float cy, float ppm, float ui)
    {
        if (_viewModel == null) return;
        float maxAge = HeatmapViewModel.TrailMaxAgeSeconds;

        foreach (var group in _viewModel.MotionTrails.GroupBy(t => t.ZoneId))
        {
            var ordered = group.OrderBy(t => t.Sequence).ToList();
            SKPoint? prev = null;
            foreach (var trail in ordered)
            {
                float fade = Math.Clamp(1f - trail.Age / maxAge, 0, 1);
                if (fade <= 0.01f) continue;

                var (east, north) = NormToWorld(trail.NormalizedX, trail.NormalizedY);
                float h = (0.03f + trail.Strength * 0.22f) * (float)_viewportMeters;
                var (head, _) = Project3DWorld(east, north, h, cx, cy, ppm);
                var (floor, _) = Project3DWorld(east, north, 0.008f * (float)_viewportMeters, cx, cy, ppm);

                SKColor lineColor = trail.IsObstacle
                    ? SKColor.Parse("#a78bfa")
                    : SKColor.Parse("#f59e0b");

                if (prev is SKPoint pPrev)
                {
                    using var link = new SKPaint
                    {
                        Color = lineColor.WithAlpha((byte)(fade * trail.Strength * 140)),
                        StrokeWidth = (1.5f + trail.Strength * 3f) * ui,
                        IsAntialias = true,
                        StrokeCap = SKStrokeCap.Round
                    };
                    canvas.DrawLine(pPrev, head, link);
                }

                using var glow = new SKPaint
                {
                    Color = lineColor.WithAlpha((byte)(fade * trail.Strength * 120)),
                    IsAntialias = true,
                    MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 4f * ui)
                };
                canvas.DrawCircle(head, (2.5f + trail.Strength * 4f) * ui, glow);

                using var core = new SKPaint
                {
                    Color = trail.IsObstacle ? SKColor.Parse("#c4b5fd") : SKColor.Parse("#ef4444"),
                    IsAntialias = true
                };
                using var floorDot = new SKPaint
                {
                    Color = (trail.IsObstacle ? SKColor.Parse("#c4b5fd") : SKColor.Parse("#ef4444")).WithAlpha((byte)(fade * 180)),
                    IsAntialias = true
                };
                float dotR = (1.2f + trail.Strength * 2.8f) * ui;
                canvas.DrawCircle(head, dotR, core);
                canvas.DrawCircle(floor, dotR * 0.65f, floorDot);

                prev = head;
            }
        }
    }

    /// <summary>"Ghost memory" layer — learned/historical patterns. Cool dashed-ring palette (blue=motion memory, slate=obstacle memory) keeps it visually distinct from warm pulsing live layers (amber/red), so the eye reads "now" vs "history" at a glance.</summary>
    private void DrawCommonHotspots3D(SKCanvas canvas, float cx, float cy, float ppm, float ui)
    {
        if (_viewModel == null) return;

        using var font = new SKFont(SKTypeface.Default, 9f * ui) { Embolden = true };
        using var text = new SKPaint { Color = SKColors.White.WithAlpha(205), IsAntialias = true };
        using var dash = SKPathEffect.CreateDash(new[] { 4.5f * ui, 3.5f * ui }, 0);

        foreach (var spot in _viewModel.CommonHotspots.OrderByDescending(s => s.Score).Take(10))
        {
            var (east, north) = NormToWorld(spot.NormalizedX, spot.NormalizedY);
            float h = (0.05f + (float)spot.Score * 0.12f) * (float)_viewportMeters;
            var (pt, _) = Project3DWorld(east, north, h, cx, cy, ppm);
            var (basePt, _) = Project3DWorld(east, north, 0, cx, cy, ppm);

            bool motion = spot.Kind == SpatialHotspotKind.Motion;
            SKColor col = motion ? SKColor.Parse("#60a5fa") : SKColor.Parse("#94a3b8");

            using var ring = new SKPaint
            {
                Color = col.WithAlpha(150),
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 1.4f * ui,
                IsAntialias = true,
                PathEffect = dash
            };
            float ringR = (7f + (float)spot.Score * 12f + Math.Min(spot.HitCount, 40) * 0.12f) * ui;
            canvas.DrawCircle(basePt, ringR, ring);

            using var fill = new SKPaint { Color = col.WithAlpha(170), IsAntialias = true };
            canvas.DrawCircle(pt, 2.6f * ui, fill);
            using var stem = new SKPaint { Color = col.WithAlpha(90), StrokeWidth = 1f * ui, IsAntialias = true };
            canvas.DrawLine(basePt, pt, stem);

            string label = $"{spot.Label} {spot.DistanceLabel} · {spot.HitCount}×";
            if (TryPlaceLabel(font, label, pt.X + ringR * 0.6f + 5f * ui, pt.Y, SKTextAlign.Left, out var lp))
                canvas.DrawText(label, lp.X, lp.Y, SKTextAlign.Left, font, text);
        }
    }

    private void DrawBlockZones3D(SKCanvas canvas, float cx, float cy, float ppm, float ui)
    {
        if (_viewModel == null) return;
        string? selected = _viewModel.SelectedZoneId;
        using var zoneFont = new SKFont(SKTypeface.Default, 10f * ui) { Embolden = true };
        using var zoneText = new SKPaint { Color = SKColors.White.WithAlpha(235), IsAntialias = true };

        foreach (var zone in _viewModel.BlockZones.OrderBy(z => z.MotionPct))
        {
            var (east, north) = NormToWorld(zone.NormalizedX, zone.NormalizedY);
            float motion = (float)Math.Clamp(zone.MotionPct / 100.0, 0, 1);
            float occ = (float)Math.Clamp(zone.OccupancyPct / 100.0, 0, 1);
            float footprint = MathF.Max(0.15f, (float)(zone.RadiusNorm * _viewportMeters * 0.9));
            float colH = (0.08f + motion * 0.55f) * (float)_viewportMeters;
            bool isSel = string.Equals(zone.ZoneId, selected, StringComparison.OrdinalIgnoreCase);

            DrawColumn3D(canvas, east, north, footprint, colH, motion, occ, isSel, cx, cy, ppm, ui);

            if (motion > 0.06 || isSel)
            {
                var (top, _) = Project3DWorld(east, north, colH + 0.06f * (float)_viewportMeters, cx, cy, ppm);
                string label = $"{zone.Name} · {zone.DistanceLabel} · {zone.BearingDeg:F0}°";
                if (TryPlaceLabel(zoneFont, label, top.X + 8 * ui, top.Y, SKTextAlign.Left, out var lp))
                {
                    canvas.DrawText(label, lp.X, lp.Y, SKTextAlign.Left, zoneFont, zoneText);
                    float barY = lp.Y + 4f * ui;
                    DrawConfidenceBars(canvas, lp.X, barY, zone.MotionPct, zone.OccupancyPct, ui);
                    _labelRects.Add(SKRect.Create(lp.X - 2f, barY - 1f, 40f * ui, 12f * ui));
                }
            }
        }
    }

    private void DrawColumn3D(SKCanvas canvas, float east, float north, float radius, float heightM,
        float motion, float occ, bool selected, float cx, float cy, float ppm, float ui)
    {
        var (basePt, _) = Project3DWorld(east, north, 0, cx, cy, ppm);
        var (topPt, _) = Project3DWorld(east, north, heightM, cx, cy, ppm);
        SKColor col = ActivityColor(motion);

        using var shaft = new SKPaint
        {
            Color = col.WithAlpha((byte)Math.Clamp(100 + motion * 155, 100, 255)),
            StrokeWidth = (6f + motion * 14f + (selected ? 4f : 0f)) * ui,
            IsAntialias = true,
            StrokeCap = SKStrokeCap.Round
        };
        canvas.DrawLine(basePt, topPt, shaft);

        using var disc = new SKPaint
        {
            Color = col.WithAlpha((byte)Math.Clamp(50 + motion * 120, 50, 200)),
            IsAntialias = true,
            MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 10f * ui)
        };
        canvas.DrawCircle(basePt, radius * ppm * 0.35f, disc);

        if (selected)
        {
            using var sel = new SKPaint
            {
                Color = SKColors.White.WithAlpha(200),
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 2.5f * ui,
                IsAntialias = true
            };
            canvas.DrawCircle(basePt, radius * ppm * 0.45f + 4f * ui, sel);
        }

        for (int r = 0; r < 3; r++)
        {
            float phase = (_animPhase + r * 0.33f) % 1f;
            float pr = radius * (0.4f + phase * 0.9f);
            using var pulse = new SKPaint
            {
                Color = SKColor.Parse("#f59e0b").WithAlpha((byte)((1f - phase) * motion * 160)),
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 1.8f * ui,
                IsAntialias = true
            };
            using var path = new SKPath();
            for (int i = 0; i <= 32; i++)
            {
                float a = i * MathF.PI * 2f / 32f;
                var (p, _) = Project3DWorld(east + pr * MathF.Sin(a), north + pr * MathF.Cos(a), 0.02f * (float)_viewportMeters, cx, cy, ppm);
                if (i == 0) path.MoveTo(p); else path.LineTo(p);
            }
            path.Close();
            canvas.DrawPath(path, pulse);
        }
    }

    private void DrawSignalProbes(SKCanvas canvas, float cx, float cy, float ppm, float ui, SKPoint you)
    {
        if (_viewModel == null) return;

        foreach (var zone in _viewModel.BlockZones.Where(z => z.MotionPct > 8))
        {
            var (east, north) = NormToWorld(zone.NormalizedX, zone.NormalizedY);
            var (target, _) = Project3DWorld(east, north, 0.05f * (float)_viewportMeters, cx, cy, ppm);
            DrawWifiSymbol(canvas, target, (float)(zone.MotionPct / 100.0), ui);
        }

        if (!_probeActive || string.IsNullOrEmpty(_probeZoneId) || _viewModel == null) return;
        var probeZone = _viewModel.BlockZones.FirstOrDefault(z =>
            string.Equals(z.ZoneId, _probeZoneId, StringComparison.OrdinalIgnoreCase));
        if (probeZone == null) return;

        var (pex, pn) = NormToWorld(probeZone.NormalizedX, probeZone.NormalizedY);
        var (end, _) = Project3DWorld(pex, pn, 0.08f * (float)_viewportMeters, cx, cy, ppm);
        float t = Math.Clamp(_probePhase, 0, 1);
        var probePt = new SKPoint(you.X + (end.X - you.X) * t, you.Y + (end.Y - you.Y) * t);

        using var beam = new SKPaint
        {
            Color = SKColor.Parse("#00f5d4").WithAlpha(180),
            StrokeWidth = 3f * ui,
            IsAntialias = true,
            PathEffect = SKPathEffect.CreateDash(new[] { 8f * ui, 6f * ui }, _animPhase * 40f)
        };
        canvas.DrawLine(you, end, beam);
        DrawWifiSymbol(canvas, probePt, 1f, ui * 1.2f);

        if (t > 0.85f)
        {
            using var echo = new SKPaint
            {
                Color = SKColor.Parse("#43e3a0").WithAlpha((byte)((t - 0.85f) / 0.15f * 200)),
                IsAntialias = true,
                MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 16f * ui)
            };
            canvas.DrawCircle(end, 20f * ui * (float)(t - 0.85f) / 0.15f, echo);
        }
    }

    private static void DrawWifiSymbol(SKCanvas canvas, SKPoint at, float strength, float ui)
    {
        using var paint = new SKPaint
        {
            Color = SKColor.Parse("#00f5d4").WithAlpha((byte)Math.Clamp(120 + strength * 135, 120, 255)),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 2f * ui,
            IsAntialias = true,
            StrokeCap = SKStrokeCap.Round
        };
        for (int i = 1; i <= 3; i++)
        {
            using var arcPath = new SKPath();
            float r = 4f * ui * i;
            arcPath.AddArc(SKRect.Create(at.X - r, at.Y - r, r * 2, r * 2), 215, 110);
            canvas.DrawPath(arcPath, paint);
        }
        using var dot = new SKPaint { Color = SKColor.Parse("#00f5d4"), IsAntialias = true };
        canvas.DrawCircle(at, 2.5f * ui, dot);
    }

    private void DrawYou3D(SKCanvas canvas, SKPoint you, float ui)
    {
        using var glow = new SKPaint
        {
            Color = SKColor.Parse("#00f5d4").WithAlpha(90),
            IsAntialias = true,
            MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 14f * ui)
        };
        using var core = new SKPaint { Color = SKColor.Parse("#00f5d4"), IsAntialias = true };
        using var ring = new SKPaint
        {
            Color = SKColors.White.WithAlpha(220),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 2.5f * ui,
            IsAntialias = true
        };
        using var font = new SKFont(SKTypeface.Default, 11f * ui) { Embolden = true };
        using var text = new SKPaint { Color = SKColors.White, IsAntialias = true };

        float pulse = 16f + 6f * MathF.Sin(_animPhase * MathF.PI * 2f);
        canvas.DrawCircle(you, pulse, glow);
        canvas.DrawCircle(you, 9f * ui, core);
        canvas.DrawCircle(you, 15f * ui, ring);
        canvas.DrawText("YOU", you.X, you.Y + 26f * ui, SKTextAlign.Center, font, text);
    }

    private void DrawCompass3D(SKCanvas canvas, float cx, float cy, float scale, float ppm, float ui)
    {
        using var font = new SKFont(SKTypeface.Default, 10f * ui) { Embolden = true };
        using var paint = new SKPaint { Color = SKColor.Parse("#8b95c9"), IsAntialias = true };
        var (n, _) = Project3D(0, (float)_viewportMeters * 1.05f, 0, cx, cy, ppm);
        canvas.DrawText("N", n.X - 4, n.Y - 6, font, paint);
    }

    /// <summary>HUD overlay text — fixed size regardless of camera zoom (`ui`); world-space labels scale, screen-space chrome must not, or lines pile up at high zoom.</summary>
    private void DrawLegend3D(SKCanvas canvas, float w, float h)
    {
        const float size = 10.5f;
        using var font = new SKFont(SKTypeface.Default, size);
        using var text = new SKPaint { Color = SKColor.Parse("#8b95c9"), IsAntialias = true };
        using var accent = new SKPaint { Color = SKColor.Parse("#f59e0b"), IsAntialias = true };
        using var bg = new SKPaint { Color = SKColor.Parse("#0a0e27").WithAlpha(170), IsAntialias = true };
        string range = _viewportMeters < 1.0 ? $"{_viewportMeters * 100:F0} cm" : $"{_viewportMeters:F1} m";

        var lines = new List<(string txt, SKPaint paint)>
        {
            ($"3D spatial · range ±{range} · drag=orbit · shift/right=pan · wheel=zoom · dblclick=reset", text),
            ($"Motion {_viewModel?.MotionConfidence:F0}% · Occ {_viewModel?.OccupancyConfidence:F0}% · CNN {_viewModel?.CnnActivityScore:F0}% · {_viewModel?.BlockZones.Count ?? 0} zones · 50ms live", accent)
        };
        string probe = _viewModel?.ProbeStatus ?? "";
        if (!string.IsNullOrWhiteSpace(probe)) lines.Add((probe, text));

        float lineH = size + 6f;
        float top = h - 8f - lines.Count * lineH;
        canvas.DrawRect(SKRect.Create(0, top - 4f, w, lines.Count * lineH + 10f), bg);
        for (int i = 0; i < lines.Count; i++)
            canvas.DrawText(lines[i].txt, 10, top + i * lineH + size, font, lines[i].paint);
    }

    private double SampleGridBilinear(float u, float v)
    {
        int size = HeatmapViewModel.FloorSize;
        float gx = u * (size - 1);
        float gy = v * (size - 1);
        int x0 = (int)Math.Floor(gx);
        int y0 = (int)Math.Floor(gy);
        int x1 = Math.Min(x0 + 1, size - 1);
        int y1 = Math.Min(y0 + 1, size - 1);
        float tx = gx - x0;
        float ty = gy - y0;
        double v00 = _floorGrid[y0, x0];
        double v10 = _floorGrid[y0, x1];
        double v01 = _floorGrid[y1, x0];
        double v11 = _floorGrid[y1, x1];
        return (1 - tx) * (1 - ty) * v00 + tx * (1 - ty) * v10 + (1 - tx) * ty * v01 + tx * ty * v11;
    }

    private static SKColor ActivityColor(float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        if (t < 0.33f) return Lerp(SKColor.Parse("#0a1628"), SKColor.Parse("#4361ee"), t / 0.33f);
        if (t < 0.66f) return Lerp(SKColor.Parse("#4361ee"), SKColor.Parse("#f59e0b"), (t - 0.33f) / 0.33f);
        return Lerp(SKColor.Parse("#f59e0b"), SKColor.Parse("#ef4444"), (t - 0.66f) / 0.34f);
    }

    private static SKColor Lerp(SKColor a, SKColor b, float t) => new(
        (byte)(a.Red + (b.Red - a.Red) * t),
        (byte)(a.Green + (b.Green - a.Green) * t),
        (byte)(a.Blue + (b.Blue - a.Blue) * t), 255);
}
