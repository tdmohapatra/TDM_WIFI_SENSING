using System.IO;
using System.Media;

namespace StarSensing.Dashboard.Services;

/// <summary>
/// Lightweight UI sound engine. Tones are synthesised in-memory (no asset files)
/// and played via <see cref="SoundPlayer"/>. Globally toggled with <see cref="Enabled"/>.
/// </summary>
public static class SoundService
{
    /// <summary>Master on/off switch bound to the toolbar toggle.</summary>
    public static bool Enabled { get; set; } = true;

    private const int SampleRate = 44100;

    private static readonly byte[] ClickWav = BuildTone(1200, 45, 0.25, 0.0);
    private static readonly byte[] NewSignalWav = BuildChord(new[] { (660, 90), (990, 120) }, 0.30);
    private static readonly byte[] AlertWav = BuildTone(300, 220, 0.35, 0.0);
    private static readonly byte[] ConnectWav = BuildChord(new[] { (523, 80), (784, 90), (1047, 120) }, 0.30);

    public static void Click() => Play(ClickWav);
    public static void NewSignal() => Play(NewSignalWav);
    public static void Connected() => Play(ConnectWav);
    public static void Alert() => Play(AlertWav);

    private static void Play(byte[] wav)
    {
        if (!Enabled) return;
        try
        {
            // Fresh player per call so overlapping events don't cut each other off badly.
            var player = new SoundPlayer(new MemoryStream(wav));
            player.Play();
        }
        catch
        {
            // Audio is non-critical; never let a sound failure break the UI.
        }
    }

    // ── WAV synthesis ──────────────────────────────────────────────────
    private static byte[] BuildTone(double freq, int ms, double amplitude, double startPhase)
    {
        int samples = SampleRate * ms / 1000;
        var pcm = new short[samples];
        for (int i = 0; i < samples; i++)
        {
            double t = (double)i / SampleRate;
            // Short attack/release envelope to avoid clicks.
            double env = Envelope(i, samples);
            double v = Math.Sin(2 * Math.PI * freq * t + startPhase) * amplitude * env;
            pcm[i] = (short)(v * short.MaxValue);
        }
        return Wrap(pcm);
    }

    private static byte[] BuildChord((int freq, int ms)[] notes, double amplitude)
    {
        int total = notes.Sum(n => SampleRate * n.ms / 1000);
        var pcm = new short[total];
        int offset = 0;
        foreach (var (freq, ms) in notes)
        {
            int samples = SampleRate * ms / 1000;
            for (int i = 0; i < samples; i++)
            {
                double t = (double)i / SampleRate;
                double env = Envelope(i, samples);
                double v = Math.Sin(2 * Math.PI * freq * t) * amplitude * env;
                pcm[offset + i] = (short)(v * short.MaxValue);
            }
            offset += samples;
        }
        return Wrap(pcm);
    }

    private static double Envelope(int i, int total)
    {
        int edge = Math.Max(1, total / 12);
        if (i < edge) return (double)i / edge;
        if (i > total - edge) return (double)(total - i) / edge;
        return 1.0;
    }

    private static byte[] Wrap(short[] pcm)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        int dataBytes = pcm.Length * 2;

        bw.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
        bw.Write(36 + dataBytes);
        bw.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));
        bw.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
        bw.Write(16);                      // fmt chunk size
        bw.Write((short)1);                // PCM
        bw.Write((short)1);                // mono
        bw.Write(SampleRate);
        bw.Write(SampleRate * 2);          // byte rate
        bw.Write((short)2);                // block align
        bw.Write((short)16);               // bits per sample
        bw.Write(System.Text.Encoding.ASCII.GetBytes("data"));
        bw.Write(dataBytes);
        foreach (var s in pcm) bw.Write(s);
        bw.Flush();
        return ms.ToArray();
    }
}
