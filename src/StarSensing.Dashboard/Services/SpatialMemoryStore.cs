using System.IO;
using System.Text.Json;
using StarSensing.Dashboard.Models;

namespace StarSensing.Dashboard.Services;

/// <summary>Long-term spatial memory for common motion paths and RSSI obstacles.</summary>
public sealed class SpatialMemoryStore
{
    public const int GridSize = 128;
    private static readonly string StorePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "StarSensing", "spatial-memory.json");

    public double[,] MotionMemory { get; } = new double[GridSize, GridSize];
    public double[,] ObstacleMemory { get; } = new double[GridSize, GridSize];

    private int _saveCounter;

    public void Decay(double factor = 0.9975)
    {
        for (int r = 0; r < GridSize; r++)
        for (int c = 0; c < GridSize; c++)
        {
            MotionMemory[r, c] *= factor;
            ObstacleMemory[r, c] *= factor;
        }
    }

    public void AccumulateMotion(double nx, double ny, double strength, int radiusCells = 3)
    {
        PaintKernel(MotionMemory, nx, ny, strength, radiusCells);
    }

    public void AccumulateObstacle(double nx, double ny, double strength, int radiusCells = 2)
    {
        PaintKernel(ObstacleMemory, nx, ny, strength, radiusCells);
    }

    private static void PaintKernel(double[,] grid, double nx, double ny, double strength, int radiusCells)
    {
        int cx = (int)Math.Round(nx * (GridSize - 1));
        int cy = (int)Math.Round(ny * (GridSize - 1));
        for (int dr = -radiusCells; dr <= radiusCells; dr++)
        for (int dc = -radiusCells; dc <= radiusCells; dc++)
        {
            int nr = cy + dr, nc = cx + dc;
            if (nr < 0 || nr >= GridSize || nc < 0 || nc >= GridSize) continue;
            double dist = Math.Sqrt(dr * dr + dc * dc);
            if (dist > radiusCells) continue;
            double falloff = 1.0 - dist / Math.Max(1, radiusCells);
            grid[nr, nc] = Math.Min(1.0, grid[nr, nc] + strength * falloff * 0.08);
        }
    }

    /// <summary>
    /// Extracts persistent hotspots and converts their grid positions to real-world polar.
    /// <paramref name="mapRadiusMeters"/> must be the FIXED calibration span used to encode
    /// positions into this normalized grid (e.g. HeatmapViewModel.MaxViewportMeters) — NOT
    /// the live zoom/viewport range. Passing the live viewport here made the reported
    /// distance for the same physical hotspot drift every time the user zoomed.
    /// </summary>
    public List<SpatialHotspot> ExtractHotspots(double mapRadiusMeters, int maxPerKind = 8)
    {
        var motion = ScanPeaks(MotionMemory, SpatialHotspotKind.Motion, mapRadiusMeters, maxPerKind);
        var obstacle = ScanPeaks(ObstacleMemory, SpatialHotspotKind.Obstacle, mapRadiusMeters, maxPerKind);
        return motion.Concat(obstacle).OrderByDescending(h => h.Score).ToList();
    }

    private static List<SpatialHotspot> ScanPeaks(double[,] grid, SpatialHotspotKind kind, double mapRadiusMeters, int max)
    {
        var candidates = new List<(int row, int col, double val)>();
        for (int r = 2; r < GridSize - 2; r++)
        {
            for (int c = 2; c < GridSize - 2; c++)
            {
                double v = grid[r, c];
                if (v < 0.06) continue;
                bool isPeak = true;
                for (int dr = -2; dr <= 2 && isPeak; dr++)
                for (int dc = -2; dc <= 2 && isPeak; dc++)
                {
                    if (dr == 0 && dc == 0) continue;
                    if (grid[r + dr, c + dc] > v) isPeak = false;
                }
                if (isPeak) candidates.Add((r, c, v));
            }
        }

        var merged = new List<SpatialHotspot>();
        foreach (var (row, col, val) in candidates.OrderByDescending(x => x.val))
        {
            double nx = col / (double)(GridSize - 1);
            double ny = row / (double)(GridSize - 1);
            if (merged.Any(h => Dist2(h.NormalizedX, h.NormalizedY, nx, ny) < 0.0025))
                continue;

            var (dist, bearing) = ToPolar(nx, ny, mapRadiusMeters);
            int hits = (int)Math.Round(val * 120);
            merged.Add(new SpatialHotspot
            {
                Kind = kind,
                Label = kind == SpatialHotspotKind.Motion ? "Movement" : "Obstacle",
                NormalizedX = nx,
                NormalizedY = ny,
                Score = val,
                HitCount = Math.Max(1, hits),
                DistanceMeters = dist,
                BearingDeg = bearing
            });
            if (merged.Count(h => h.Kind == kind) >= max) break;
        }

        return merged.Where(h => h.Kind == kind).ToList();
    }

    private static (double DistM, double BearingDeg) ToPolar(double nx, double ny, double mapRadiusMeters)
    {
        double east = (nx - 0.5) * 2.0 * mapRadiusMeters;
        double north = (ny - 0.5) * 2.0 * mapRadiusMeters;
        double dist = Math.Sqrt(east * east + north * north);
        double bearing = BearingStoreService.NormalizeDeg(Math.Atan2(east, north) * 180.0 / Math.PI);
        return (dist, bearing);
    }

    private static double Dist2(double x1, double y1, double x2, double y2)
    {
        double dx = x1 - x2, dy = y1 - y2;
        return dx * dx + dy * dy;
    }

    public void RequestSave()
    {
        if (++_saveCounter % 40 != 0) return;
        _ = SaveAsync();
    }

    public async Task LoadAsync()
    {
        try
        {
            if (!File.Exists(StorePath)) return;
            await using var fs = File.OpenRead(StorePath);
            var doc = await JsonSerializer.DeserializeAsync<SpatialMemoryDto>(fs);
            if (doc == null) return;
            CopyFlat(doc.Motion, MotionMemory);
            CopyFlat(doc.Obstacle, ObstacleMemory);
        }
        catch { }
    }

    public async Task SaveAsync()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
            var dto = new SpatialMemoryDto
            {
                Motion = Flatten(MotionMemory),
                Obstacle = Flatten(ObstacleMemory),
                SavedAt = DateTimeOffset.UtcNow
            };
            await using var fs = File.Create(StorePath);
            await JsonSerializer.SerializeAsync(fs, dto, new JsonSerializerOptions { WriteIndented = false });
        }
        catch { }
    }

    public async Task ClearAsync()
    {
        Array.Clear(MotionMemory, 0, MotionMemory.Length);
        Array.Clear(ObstacleMemory, 0, ObstacleMemory.Length);
        try { if (File.Exists(StorePath)) File.Delete(StorePath); } catch { }
        await Task.CompletedTask;
    }

    private static double[] Flatten(double[,] grid)
    {
        var flat = new double[GridSize * GridSize];
        for (int r = 0; r < GridSize; r++)
        for (int c = 0; c < GridSize; c++)
            flat[r * GridSize + c] = grid[r, c];
        return flat;
    }

    private static void CopyFlat(double[]? flat, double[,] grid)
    {
        if (flat == null || flat.Length != GridSize * GridSize) return;
        for (int r = 0; r < GridSize; r++)
        for (int c = 0; c < GridSize; c++)
            grid[r, c] = flat[r * GridSize + c];
    }

    private sealed class SpatialMemoryDto
    {
        public double[]? Motion { get; set; }
        public double[]? Obstacle { get; set; }
        public DateTimeOffset SavedAt { get; set; }
    }
}
