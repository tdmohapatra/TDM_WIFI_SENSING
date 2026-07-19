using System.Windows.Controls;
using SkiaSharp;
using StarSensing.Core.Protos;
using StarSensing.Dashboard.ViewModels;
using ScottPlot.Plottables;

namespace StarSensing.Dashboard.Views
{
    public partial class MotionView : UserControl
    {
        private MotionViewModel? _viewModel;
        private double _confidence;
        private DataStreamer? _timelineStreamer;

        public MotionView()
        {
            InitializeComponent();
            SetupTimeline();
            DataContextChanged += OnDataContextChanged;
        }

        private void SetupTimeline()
        {
            _timelineStreamer = TimelinePlot.Plot.Add.DataStreamer(60);
            _timelineStreamer.AddRange(Enumerable.Repeat(0.0, 60).ToArray());

            TimelinePlot.Plot.Title("Motion Confidence");
            TimelinePlot.Plot.YLabel("%");
            TimelinePlot.Plot.Axes.SetLimitsY(0, 100);
            TimelinePlot.Refresh();
        }

        private void OnDataContextChanged(object sender, System.Windows.DependencyPropertyChangedEventArgs e)
        {
            if (_viewModel != null)
            {
                _viewModel.OnStateReceived -= ViewModel_OnStateReceived;
                _viewModel.OnReplayConfidence -= ViewModel_OnReplayConfidence;
                _viewModel.OnTimelineSeriesReady -= ViewModel_OnTimelineSeriesReady;
            }

            _viewModel = e.NewValue as MotionViewModel;

            if (_viewModel != null)
            {
                _viewModel.OnStateReceived += ViewModel_OnStateReceived;
                _viewModel.OnReplayConfidence += ViewModel_OnReplayConfidence;
                _viewModel.OnTimelineSeriesReady += ViewModel_OnTimelineSeriesReady;
            }
        }

        private void OnRate50Clicked(object sender, System.Windows.RoutedEventArgs e)
        {
            if (_viewModel != null)
                _viewModel.SampleIntervalMs = 50;
        }

        private void OnRate100Clicked(object sender, System.Windows.RoutedEventArgs e)
        {
            if (_viewModel != null)
                _viewModel.SampleIntervalMs = 100;
        }

        private void ViewModel_OnStateReceived(EnvironmentStateMsg state)
        {
            _confidence = (_viewModel?.MotionConfidence ?? state.MotionConfidence) * 100.0;
            _timelineStreamer?.Add(_confidence);
            GaugeCanvas.InvalidateVisual();
            TimelinePlot.Refresh();
        }

        private void ViewModel_OnReplayConfidence(double confidence)
        {
            _confidence = confidence * 100.0;
            _timelineStreamer?.Add(_confidence);
            GaugeCanvas.InvalidateVisual();
            TimelinePlot.Refresh();
        }

        private void ViewModel_OnTimelineSeriesReady(IReadOnlyList<(DateTimeOffset Time, double Confidence)> series)
        {
            Dispatcher.Invoke(() =>
            {
                if (series.Count == 0) return;
                TimelinePlot.Plot.Clear();
                var xs = series.Select(p => p.Time.UtcDateTime.ToOADate()).ToArray();
                var ys = series.Select(p => p.Confidence * 100.0).ToArray();
                TimelinePlot.Plot.Add.Scatter(xs, ys);
                TimelinePlot.Plot.Axes.DateTimeTicksBottom();
                TimelinePlot.Plot.Axes.SetLimitsY(0, 100);
                TimelinePlot.Plot.Title("Motion Confidence (time series)");
                TimelinePlot.Refresh();
            });
        }

        private void OnPaintGauge(object sender, SkiaSharp.Views.Desktop.SKPaintSurfaceEventArgs e)
        {
            var canvas = e.Surface.Canvas;
            float w = e.Info.Width;
            float h = e.Info.Height;

            canvas.Clear(SKColor.Parse("#151a3d"));

            float cx = w / 2f;
            float cy = h / 2f * 1.05f;
            float gaugeRadius = MathF.Min(w, h) * 0.38f;

            DrawGaugeBackground(canvas, cx, cy, gaugeRadius);
            DrawGaugeArc(canvas, cx, cy, gaugeRadius);
            DrawGaugeCenter(canvas, cx, cy, gaugeRadius);
        }

        private static void DrawGaugeBackground(SKCanvas canvas, float cx, float cy, float r)
        {
            const float arcStroke = 18f;
            const float startDeg  = 145f;
            const float spanDeg   = 250f;

            using var bgPaint = new SKPaint
            {
                Color       = SKColor.Parse("#1e2545"),
                Style       = SKPaintStyle.Stroke,
                StrokeWidth = arcStroke,
                StrokeCap   = SKStrokeCap.Round,
                IsAntialias = true
            };

            var rect = new SKRect(cx - r, cy - r, cx + r, cy + r);
            canvas.DrawArc(rect, startDeg, spanDeg, false, bgPaint);

            // Tick marks
            using var tickPaint = new SKPaint
            {
                Color = SKColor.Parse("#2a3060"),
                StrokeWidth = 2,
                IsAntialias = true
            };
            using var tickFont  = new SKFont(SKTypeface.Default, 10f);
            using var tickLabel = new SKPaint { Color = SKColor.Parse("#4a5080"), IsAntialias = true };

            for (int pct = 0; pct <= 100; pct += 10)
            {
                float angle = (startDeg + pct / 100f * spanDeg) * MathF.PI / 180f;
                float inner = r - 22f;
                float outer = r - 10f;
                canvas.DrawLine(
                    cx + inner * MathF.Cos(angle), cy + inner * MathF.Sin(angle),
                    cx + outer * MathF.Cos(angle), cy + outer * MathF.Sin(angle),
                    tickPaint);

                if (pct % 25 == 0)
                {
                    float lx = cx + (r - 38f) * MathF.Cos(angle);
                    float ly = cy + (r - 38f) * MathF.Sin(angle);
                    canvas.DrawText($"{pct}", lx, ly + 4f, SKTextAlign.Center, tickFont, tickLabel);
                }
            }
        }

        private void DrawGaugeArc(SKCanvas canvas, float cx, float cy, float r)
        {
            const float arcStroke = 18f;
            const float startDeg  = 145f;
            const float spanDeg   = 250f;

            float pct = (float)Math.Clamp(_confidence, 0, 100);

            // Color gradient: green → amber → orange → red
            SKColor arcColor = pct < 30
                ? SKColor.Parse("#22c55e")
                : pct < 60
                    ? SKColor.Parse("#f59e0b")
                    : pct < 80
                        ? SKColor.Parse("#f97316")
                        : SKColor.Parse("#ef4444");

            using var arcPaint = new SKPaint
            {
                Color       = arcColor,
                Style       = SKPaintStyle.Stroke,
                StrokeWidth = arcStroke,
                StrokeCap   = SKStrokeCap.Round,
                IsAntialias = true,
                MaskFilter  = SKMaskFilter.CreateBlur(SKBlurStyle.Solid, 3f)
            };

            var rect = new SKRect(cx - r, cy - r, cx + r, cy + r);
            if (pct > 0.5f)
                canvas.DrawArc(rect, startDeg, pct / 100f * spanDeg, false, arcPaint);

            // Leading edge bright dot
            float leadAngle = (startDeg + pct / 100f * spanDeg) * MathF.PI / 180f;
            using var dotPaint = new SKPaint
            {
                Color = SKColors.White.WithAlpha(220),
                IsAntialias = true,
                MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 4f)
            };
            canvas.DrawCircle(cx + r * MathF.Cos(leadAngle), cy + r * MathF.Sin(leadAngle), 6f, dotPaint);
        }

        private void DrawGaugeCenter(SKCanvas canvas, float cx, float cy, float r)
        {
            float pct = (float)Math.Clamp(_confidence, 0, 100);

            SKColor valueColor = pct < 30
                ? SKColor.Parse("#22c55e")
                : pct < 60
                    ? SKColor.Parse("#f59e0b")
                    : pct < 80
                        ? SKColor.Parse("#f97316")
                        : SKColor.Parse("#ef4444");

            // Large percentage value
            using var bigFont  = new SKFont(SKTypeface.Default, r * 0.42f) { Embolden = true };
            using var bigPaint = new SKPaint { Color = valueColor, IsAntialias = true };
            canvas.DrawText($"{pct:F0}", cx, cy - r * 0.05f, SKTextAlign.Center, bigFont, bigPaint);

            // "%" sub-label
            using var subFont  = new SKFont(SKTypeface.Default, r * 0.18f);
            using var subPaint = new SKPaint { Color = SKColor.Parse("#7080b0"), IsAntialias = true };
            canvas.DrawText("Motion Confidence", cx, cy + r * 0.22f, SKTextAlign.Center, subFont, subPaint);
        }
    }
}
