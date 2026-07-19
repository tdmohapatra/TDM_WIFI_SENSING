using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using SkiaSharp;
using StarSensing.Dashboard.Services;
using StarSensing.Dashboard.ViewModels;

namespace StarSensing.Dashboard.Views;

public partial class SolidView : UserControl
{
    private readonly DispatcherTimer _renderTimer;
    private SolidViewViewModel? _vm;

    // 3D camera
    private float _yaw = 0.6f;
    private float _elev = 0.55f;
    private float _zoom = 1f;
    private bool _dragging;
    private Point _lastDrag;

    private readonly string _saveFolder;

    public SolidView()
    {
        InitializeComponent();

        _saveFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            "StarSensing", "SolidView");
        Directory.CreateDirectory(_saveFolder);

        _renderTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        _renderTimer.Tick += (_, _) => SolidCanvas.InvalidateVisual();
        _renderTimer.Start();

        SolidCanvas.MouseLeftButtonDown += OnDown;
        SolidCanvas.MouseLeftButtonUp += OnUp;
        SolidCanvas.MouseMove += OnMove;

        DataContextChanged += (_, e) => _vm = e.NewValue as SolidViewViewModel;
    }

    // ── Orbit / zoom ───────────────────────────────────────────────────
    private void OnDown(object s, MouseButtonEventArgs e) { _dragging = true; _lastDrag = e.GetPosition(SolidCanvas); SolidCanvas.CaptureMouse(); }
    private void OnUp(object s, MouseButtonEventArgs e) { _dragging = false; SolidCanvas.ReleaseMouseCapture(); }
    private void OnMove(object s, MouseEventArgs e)
    {
        if (!_dragging) return;
        var p = e.GetPosition(SolidCanvas);
        _yaw += (float)(p.X - _lastDrag.X) * 0.01f;
        _elev = Math.Clamp(_elev + (float)(p.Y - _lastDrag.Y) * 0.01f, 0.1f, 1.45f);
        _lastDrag = p;
        SolidCanvas.InvalidateVisual();
    }

    private void OnCanvasWheel(object sender, MouseWheelEventArgs e)
    {
        _zoom = Math.Clamp(_zoom * (e.Delta > 0 ? 1.12f : 0.89f), 0.3f, 6f);
        SolidCanvas.InvalidateVisual();
        e.Handled = true;
    }

    private void OnPaintSolid(object sender, SkiaSharp.Views.Desktop.SKPaintSurfaceEventArgs e)
        => Draw(e.Surface.Canvas, e.Info.Width, e.Info.Height);

    // ── 3D projection ──────────────────────────────────────────────────
    private SKPoint Project(float x, float y, float z, float cx, float cy, float scale, out float depth)
    {
        float cyaw = MathF.Cos(_yaw), syaw = MathF.Sin(_yaw);
        float xr = x * cyaw - z * syaw;
        float zr = x * syaw + z * cyaw;
        float se = MathF.Sin(_elev), ce = MathF.Cos(_elev);
        depth = zr * ce - y * se;
        return new SKPoint(cx + xr * scale, cy - (zr * se + y * ce) * scale);
    }

    // ── Render ─────────────────────────────────────────────────────────
    private void Draw(SKCanvas canvas, float w, float h)
    {
        canvas.Clear(SKColor.Parse("#070a1f"));
        if (_vm == null) return;

        var series = _vm.GetSeries();
        float cx = w / 2f;
        float cy = h * 0.66f;
        float scale = MathF.Min(w, h) * 0.05f * _zoom;

        const float worldW = 14f;     // time axis span (world units)
        const float laneGap = 3.2f;   // distance between network lanes
        const float maxH = 7f;        // max RSSI height

        if (series.Count == 0)
        {
            using var hf = new SKFont(SKTypeface.Default, 16f);
            using var hp = new SKPaint { Color = SKColor.Parse("#8b95c9"), IsAntialias = true };
            canvas.DrawText("Select Wi-Fi networks in Signal Monitor — their live waveforms render here in 3D.",
                cx, h * 0.4f, SKTextAlign.Center, hf, hp);
            canvas.DrawText("Drag to rotate · Wheel to zoom · pick a Window · Load History to backfill.",
                cx, h * 0.4f + 26, SKTextAlign.Center, hf, hp);
            return;
        }

        int n = series.Count;
        float laneSpan = (n - 1) * laneGap / 2f;

        DrawFloor(canvas, cx, cy, scale, worldW, laneSpan, laneGap, maxH);

        // Back-to-front lane ordering.
        var order = Enumerable.Range(0, n)
            .OrderByDescending(i =>
            {
                Project(0, 0, i * laneGap - laneSpan, cx, cy, scale, out var d);
                return d;
            })
            .ToList();

        using var labelFont = new SKFont(SKTypeface.Default, 12f) { Embolden = true };
        using var labelPaint = new SKPaint { Color = SKColors.White.WithAlpha(220), IsAntialias = true };

        foreach (int i in order)
        {
            var s = series[i];
            float z = i * laneGap - laneSpan;
            SKColor laneColor = SKColor.Parse(s.ColorHex);

            // Map points to world space across the window.
            long first = s.Points[0].Ms;
            long last = s.Points[^1].Ms;
            float span = Math.Max(1, last - first);

            var screen = new List<(SKPoint top, SKPoint baseP, int rssi)>(s.Points.Count);
            foreach (var (ms, rssi) in s.Points)
            {
                float tNorm = (ms - first) / span;            // 0..1
                float x = (tNorm - 0.5f) * worldW;
                float hh = MapHeight(rssi, maxH);
                var top = Project(x, hh, z, cx, cy, scale, out _);
                var bp = Project(x, 0, z, cx, cy, scale, out _);
                screen.Add((top, bp, rssi));
            }

            // Ribbon fill between consecutive samples, coloured by RSSI strength.
            using var fill = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill };
            for (int k = 1; k < screen.Count; k++)
            {
                using var quad = new SKPath();
                quad.MoveTo(screen[k - 1].baseP);
                quad.LineTo(screen[k - 1].top);
                quad.LineTo(screen[k].top);
                quad.LineTo(screen[k].baseP);
                quad.Close();
                fill.Color = RssiColor((screen[k - 1].rssi + screen[k].rssi) / 2).WithAlpha(150);
                canvas.DrawPath(quad, fill);
            }

            // Top crest line in the network's own colour.
            using var crest = new SKPaint
            {
                IsAntialias = true,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 2.2f,
                StrokeCap = SKStrokeCap.Round,
                Color = laneColor
            };
            using var crestPath = new SKPath();
            for (int k = 0; k < screen.Count; k++)
            {
                if (k == 0) crestPath.MoveTo(screen[k].top);
                else crestPath.LineTo(screen[k].top);
            }
            canvas.DrawPath(crestPath, crest);

            // Lane label at the latest crest point.
            var label = screen[^1].top;
            string name = string.IsNullOrWhiteSpace(s.Ssid) ? s.Bssid : s.Ssid;
            canvas.DrawText($"{name}  {screen[^1].rssi} dBm  ({s.Points.Count})",
                label.X + 8, label.Y - 6, SKTextAlign.Left, labelFont, labelPaint);
        }

        DrawAxisLabels(canvas, cx, cy, scale, worldW, laneSpan, maxH);
    }

    private void DrawFloor(SKCanvas canvas, float cx, float cy, float scale, float worldW, float laneSpan, float laneGap, float maxH)
    {
        using var grid = new SKPaint { Color = SKColor.Parse("#26305f").WithAlpha(150), Style = SKPaintStyle.Stroke, StrokeWidth = 1f, IsAntialias = true };
        float zMin = -laneSpan - laneGap * 0.6f;
        float zMax = laneSpan + laneGap * 0.6f;

        // Time gridlines (along z).
        for (int g = 0; g <= 10; g++)
        {
            float x = (g / 10f - 0.5f) * worldW;
            var a = Project(x, 0, zMin, cx, cy, scale, out _);
            var b = Project(x, 0, zMax, cx, cy, scale, out _);
            canvas.DrawLine(a, b, grid);
        }
        // Lane gridlines (along x).
        for (float z = zMin; z <= zMax + 0.01f; z += laneGap)
        {
            var a = Project(-worldW / 2, 0, z, cx, cy, scale, out _);
            var b = Project(worldW / 2, 0, z, cx, cy, scale, out _);
            canvas.DrawLine(a, b, grid);
        }
    }

    private void DrawAxisLabels(SKCanvas canvas, float cx, float cy, float scale, float worldW, float laneSpan, float maxH)
    {
        using var font = new SKFont(SKTypeface.Default, 11f);
        using var paint = new SKPaint { Color = SKColor.Parse("#8b95c9"), IsAntialias = true };

        var tStart = Project(-worldW / 2, 0, laneSpan + 1.4f, cx, cy, scale, out _);
        var tEnd = Project(worldW / 2, 0, laneSpan + 1.4f, cx, cy, scale, out _);
        canvas.DrawText($"t-{_vm?.WindowLabel}", tStart.X, tStart.Y + 14, SKTextAlign.Center, font, paint);
        canvas.DrawText("now", tEnd.X, tEnd.Y + 14, SKTextAlign.Center, font, paint);

        // RSSI height legend.
        var hTop = Project(-worldW / 2 - 0.6f, maxH, laneSpan + 1.4f, cx, cy, scale, out _);
        var hBot = Project(-worldW / 2 - 0.6f, 0, laneSpan + 1.4f, cx, cy, scale, out _);
        canvas.DrawText("-25 dBm", hTop.X - 6, hTop.Y, SKTextAlign.Right, font, paint);
        canvas.DrawText("-95 dBm", hBot.X - 6, hBot.Y, SKTextAlign.Right, font, paint);
    }

    private static float MapHeight(int rssi, float maxH)
    {
        float t = Math.Clamp((rssi + 95f) / 70f, 0f, 1f);   // -95..-25 → 0..1
        return 0.15f + t * maxH;
    }

    private static SKColor RssiColor(int rssi)
    {
        float t = Math.Clamp((rssi + 90f) / 60f, 0f, 1f);    // 0 weak .. 1 strong
        // red → amber → cyan-green
        if (t < 0.5f)
        {
            float u = t / 0.5f;
            return Lerp(SKColor.Parse("#ef4444"), SKColor.Parse("#f59e0b"), u);
        }
        float v = (t - 0.5f) / 0.5f;
        return Lerp(SKColor.Parse("#f59e0b"), SKColor.Parse("#00f5d4"), v);
    }

    private static SKColor Lerp(SKColor a, SKColor b, float t) => new(
        (byte)(a.Red + (b.Red - a.Red) * t),
        (byte)(a.Green + (b.Green - a.Green) * t),
        (byte)(a.Blue + (b.Blue - a.Blue) * t),
        255);

    // ── Save / stitch / open ──────────────────────────────────────────
    private void OnSaveImageClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            const int sw = 1700, sh = 1000;
            using var surface = SKSurface.Create(new SKImageInfo(sw, sh));
            Draw(surface.Canvas, sw, sh);
            using var img = surface.Snapshot();
            using var data = img.Encode(SKEncodedImageFormat.Png, 95);

            string file = Path.Combine(_saveFolder, $"solid_{DateTime.Now:yyyyMMdd_HHmmss_fff}.png");
            using (var fs = File.OpenWrite(file)) data.SaveTo(fs);

            if (_vm != null)
            {
                _vm.SavedImageCount++;
                _vm.StatusText = $"Saved {Path.GetFileName(file)}";
            }
            SoundService.Connected();
        }
        catch (Exception ex)
        {
            if (_vm != null) _vm.StatusText = $"Save failed: {ex.Message}";
        }
    }

    private void OnStitchClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            var files = Directory.GetFiles(_saveFolder, "solid_*.png").OrderBy(f => f).ToList();
            if (files.Count == 0)
            {
                if (_vm != null) _vm.StatusText = "No saved images to stitch. Save some first.";
                return;
            }

            int cols = (int)Math.Ceiling(Math.Sqrt(files.Count));
            int rows = (int)Math.Ceiling((double)files.Count / cols);
            const int cellW = 560, cellH = 330, pad = 8;
            int W = cols * cellW + (cols + 1) * pad;
            int H = rows * cellH + (rows + 1) * pad;

            using var surface = SKSurface.Create(new SKImageInfo(W, H));
            var canvas = surface.Canvas;
            canvas.Clear(SKColor.Parse("#070a1f"));

            for (int idx = 0; idx < files.Count; idx++)
            {
                using var bmp = SKBitmap.Decode(files[idx]);
                if (bmp == null) continue;
                int r = idx / cols, c = idx % cols;
                float x = pad + c * (cellW + pad);
                float y = pad + r * (cellH + pad);
                canvas.DrawBitmap(bmp, new SKRect(x, y, x + cellW, y + cellH));
            }

            using var img = surface.Snapshot();
            using var data = img.Encode(SKEncodedImageFormat.Png, 95);
            string outFile = Path.Combine(_saveFolder, $"reference_{DateTime.Now:yyyyMMdd_HHmmss}.png");
            using (var fs = File.OpenWrite(outFile)) data.SaveTo(fs);

            if (_vm != null) _vm.StatusText = $"Stitched {files.Count} images → {Path.GetFileName(outFile)}";
        }
        catch (Exception ex)
        {
            if (_vm != null) _vm.StatusText = $"Stitch failed: {ex.Message}";
        }
    }

    private void OnOpenFolderClicked(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", _saveFolder) { UseShellExecute = true }); }
        catch { }
    }
}
