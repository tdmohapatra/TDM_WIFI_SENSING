using System;
using System.Collections.Generic;
using System.Windows.Controls;
using System.Windows.Threading;
using SkiaSharp;
using StarSensing.Core.Protos;
using StarSensing.Dashboard.ViewModels;

namespace StarSensing.Dashboard.Views
{
    public partial class RadarView : UserControl
    {
        private RadarViewModel? _viewModel;
        private List<SignalMeasurementMsg> _latestMeasurements = new();
        private readonly Dictionary<string, double> _varianceByBssid = new();
        private float _sweepAngle = 0f;
        private readonly DispatcherTimer _timer;

        public RadarView()
        {
            InitializeComponent();

            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
            _timer.Tick += (s, e) =>
            {
                _sweepAngle = (_sweepAngle + 2f) % 360f;
                SkiaCanvas.InvalidateVisual();
            };
            _timer.Start();

            DataContextChanged += OnDataContextChanged;
        }

        private void OnDataContextChanged(object sender, System.Windows.DependencyPropertyChangedEventArgs e)
        {
            if (_viewModel != null)
            {
                _viewModel.OnDataReceived -= ViewModel_OnDataReceived;
                _viewModel.OnStateReceived -= ViewModel_OnStateReceived;
            }
            _viewModel = e.NewValue as RadarViewModel;
            if (_viewModel != null)
            {
                _viewModel.OnDataReceived += ViewModel_OnDataReceived;
                _viewModel.OnStateReceived += ViewModel_OnStateReceived;
            }
        }

        private void ViewModel_OnStateReceived(EnvironmentStateMsg state)
        {
            Dispatcher.Invoke(() =>
            {
                foreach (var s in state.Signals)
                    _varianceByBssid[s.Bssid] = s.Variance;
            });
        }

        private void ViewModel_OnDataReceived(MeasurementBatch batch)
        {
            Dispatcher.Invoke(() => _latestMeasurements = new List<SignalMeasurementMsg>(batch.Measurements));
        }

        private void OnPaintSurface(object sender, SkiaSharp.Views.Desktop.SKPaintSurfaceEventArgs e)
        {
            var canvas = e.Surface.Canvas;
            float width  = e.Info.Width;
            float height = e.Info.Height;
            float cx = width  / 2f;
            float cy = height / 2f;
            float radius = MathF.Min(cx, cy) - 30f;

            canvas.Clear(SKColor.Parse("#0d1117"));

            DrawGrid(canvas, cx, cy, radius);
            DrawSweep(canvas, cx, cy, radius);
            DrawBlips(canvas, cx, cy, radius);
        }

        private static void DrawGrid(SKCanvas canvas, float cx, float cy, float radius)
        {
            using var paint = new SKPaint
            {
                Color = SKColor.Parse("#00f5d4").WithAlpha(40),
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 1,
                IsAntialias = true
            };
            using var labelFont  = new SKFont(SKTypeface.Default, 10f);
            using var labelPaint = new SKPaint { Color = SKColor.Parse("#00f5d4").WithAlpha(80), IsAntialias = true };

            for (int ring = 1; ring <= 4; ring++)
            {
                float r = radius * (ring / 4f);
                canvas.DrawCircle(cx, cy, r, paint);

                // RSSI label on each ring (inner = strong, outer = weak)
                int rssi = -20 - (ring - 1) * 20; // -20, -40, -60, -80
                canvas.DrawText($"{rssi}", cx + r + 2, cy - 2, SKTextAlign.Left, labelFont, labelPaint);
            }

            // Crosshair lines
            canvas.DrawLine(cx, cy - radius, cx, cy + radius, paint);
            canvas.DrawLine(cx - radius, cy, cx + radius, cy, paint);

            // Diagonal lines
            float d = radius * 0.707f;
            canvas.DrawLine(cx - d, cy - d, cx + d, cy + d, paint);
            canvas.DrawLine(cx + d, cy - d, cx - d, cy + d, paint);
        }

        private void DrawSweep(SKCanvas canvas, float cx, float cy, float radius)
        {
            const float sweepWidth = 50f;

            using var shader = SKShader.CreateSweepGradient(
                new SKPoint(cx, cy),
                new[]
                {
                    SKColor.Parse("#00f5d4").WithAlpha(0),
                    SKColor.Parse("#00f5d4").WithAlpha(0),
                    SKColor.Parse("#00f5d4").WithAlpha(180),
                    SKColor.Parse("#00f5d4").WithAlpha(0),
                },
                new[] { 0f, (_sweepAngle - sweepWidth) / 360f, _sweepAngle / 360f, 1f }
                    .Select(f => Math.Max(0f, Math.Min(1f, f))).ToArray()
            );

            using var sweepPaint = new SKPaint
            {
                Shader  = shader,
                Style   = SKPaintStyle.Fill,
                IsAntialias = true
            };

            // Full circle filled with the sweep gradient
            canvas.DrawCircle(cx, cy, radius, sweepPaint);

            // Solid leading edge line
            float leadRad = _sweepAngle * MathF.PI / 180f;
            using var linePaint = new SKPaint
            {
                Color = SKColor.Parse("#00f5d4").WithAlpha(220),
                StrokeWidth = 2,
                IsAntialias = true
            };
            canvas.DrawLine(cx, cy,
                cx + radius * MathF.Cos(leadRad),
                cy + radius * MathF.Sin(leadRad),
                linePaint);
        }

        private void DrawBlips(SKCanvas canvas, float cx, float cy, float radius)
        {
            using var labelFont  = new SKFont(SKTypeface.Default, 11f);
            using var labelPaint = new SKPaint { Color = SKColors.White, IsAntialias = true };

            foreach (var m in _latestMeasurements)
            {
                // Stable pseudo-random angle from BSSID hash
                uint hash = unchecked((uint)m.Bssid.GetHashCode());
                float angle = (hash % 360) * MathF.PI / 180f;

                // RSSI → radius: closer = stronger
                float rssiClamped = Math.Max(-100f, Math.Min(-20f, m.RssiDbm));
                float dist = 1f - (rssiClamped + 100f) / 80f;
                float r = radius * dist;

                float px = cx + r * MathF.Cos(angle);
                float py = cy + r * MathF.Sin(angle);

                // How close is this blip's angle to the current sweep? (in degrees)
                float blipDeg = (hash % 360);
                float diff    = Math.Abs(blipDeg - _sweepAngle) % 360f;
                if (diff > 180f) diff = 360f - diff;
                float glow = diff < 60f ? 1f - diff / 60f : 0f;

                // Color by signal strength
                SKColor blipColor = rssiClamped > -50
                    ? SKColor.Parse("#00f5d4")  // strong: cyan
                    : rssiClamped > -70
                        ? SKColor.Parse("#f5a623")  // medium: amber
                        : SKColor.Parse("#ef4444"); // weak: red

                // Outer glow
                if (glow > 0.01f)
                {
                    using var glowPaint = new SKPaint
                    {
                        Color = blipColor.WithAlpha((byte)(glow * 100)),
                        IsAntialias = true,
                        MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 8f)
                    };
                    canvas.DrawCircle(px, py, 14f, glowPaint);
                }

                // Blip dot — size scales with ML variance (motion activity)
                _varianceByBssid.TryGetValue(m.Bssid, out double variance);
                float dotR = 4f + (float)Math.Min(8.0, variance);
                using var blipPaint = new SKPaint
                {
                    Color = blipColor.WithAlpha((byte)(80 + glow * 175)),
                    IsAntialias = true
                };
                canvas.DrawCircle(px, py, dotR, blipPaint);

                if (variance > 3.0)
                {
                    using var varPaint = new SKPaint { Color = SKColor.Parse("#f97316").WithAlpha(160), IsAntialias = true };
                    canvas.DrawCircle(px, py, dotR + 4f, varPaint);
                }

                // SSID label for stronger signals
                if (m.RssiDbm > -70)
                {
                    labelPaint.Color = SKColors.White.WithAlpha((byte)(100 + glow * 155));
                    canvas.DrawText(string.IsNullOrEmpty(m.Ssid) ? m.Bssid[..8] : m.Ssid,
                        px + 8f, py + 4f, SKTextAlign.Left, labelFont, labelPaint);
                }
            }

            // Center dot
            using var centerPaint = new SKPaint { Color = SKColor.Parse("#00f5d4"), IsAntialias = true };
            canvas.DrawCircle(cx, cy, 4f, centerPaint);

            // "You" label
            labelPaint.Color = SKColor.Parse("#00f5d4");
            canvas.DrawText("YOU", cx + 6f, cy - 6f, SKTextAlign.Left, labelFont, labelPaint);
        }
    }
}
