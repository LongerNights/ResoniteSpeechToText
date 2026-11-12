using System.Text;

namespace SpeechToText.Tests;

internal static class Repo
{
    public static readonly string Root = FindRoot();
    public static string Fixture(string name) => Path.Combine(Root, "tests", "fixtures", name);
    public static string Model(string name) => Path.Combine(Root, "models", name);

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "SPEC.md"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repo root (SPEC.md) not found above " + AppContext.BaseDirectory);
    }
}

/// <summary>[Fact] that is skipped (and says why) when a model file hasn't been downloaded.</summary>
public sealed class RequiresModelFactAttribute : FactAttribute
{
    public RequiresModelFactAttribute(params string[] models)
    {
        foreach (var m in models)
            if (!File.Exists(Repo.Model(m))) { Skip = $"model not found: models\\{m} (see CLAUDE.md)"; return; }
    }
}

/// <summary>[Fact] that talks to the real internet; skipped unless STT_NETWORK_TESTS=1.</summary>
public sealed class NetworkFactAttribute : FactAttribute
{
    public NetworkFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("STT_NETWORK_TESTS") != "1") Skip = "network test: set STT_NETWORK_TESTS=1 to run";
    }
}

internal static class Audio
{
    public const int Rate = 16000;

    /// <summary>Reads a 16-bit PCM mono WAV, walking the chunk list so extra header chunks are fine.</summary>
    public static float[] ReadWav(string path)
    {
        var b = File.ReadAllBytes(path);
        if (Encoding.ASCII.GetString(b, 0, 4) != "RIFF") throw new InvalidDataException("not a WAV");
        int pos = 12, rate = 0, bits = 0, channels = 0;
        while (pos + 8 <= b.Length)
        {
            string id = Encoding.ASCII.GetString(b, pos, 4);
            int size = BitConverter.ToInt32(b, pos + 4);
            if (id == "fmt ") { channels = BitConverter.ToInt16(b, pos + 10); rate = BitConverter.ToInt32(b, pos + 12); bits = BitConverter.ToInt16(b, pos + 22); }
            else if (id == "data")
            {
                if (rate != Rate || bits != 16 || channels != 1) throw new InvalidDataException($"expected 16 kHz mono 16-bit, got {rate}/{channels}/{bits}");
                int n = Math.Min(size, b.Length - pos - 8) / 2;
                var s = new float[n];
                for (int i = 0; i < n; i++) s[i] = BitConverter.ToInt16(b, pos + 8 + i * 2) / 32768f;
                return s;
            }
            pos += 8 + size + (size & 1);
        }
        throw new InvalidDataException("no data chunk");
    }

    public static float[] Silence(double seconds) => new float[(int)(seconds * Rate)];

    public static float[] Sine(double hz, double seconds, double amplitude, int rate = Rate)
    {
        var s = new float[(int)(seconds * rate)];
        for (int i = 0; i < s.Length; i++) s[i] = (float)(amplitude * Math.Sin(2 * Math.PI * hz * i / rate));
        return s;
    }

    /// <summary>Gaussian white noise at the given RMS level in dBFS (seeded, so tests are deterministic).</summary>
    public static float[] Noise(double seconds, double rmsDb, int seed = 1)
    {
        var rnd = new Random(seed);
        double rms = Math.Pow(10, rmsDb / 20);
        var s = new float[(int)(seconds * Rate)];
        for (int i = 0; i < s.Length; i++)
        {
            double u1 = 1 - rnd.NextDouble(), u2 = rnd.NextDouble();
            s[i] = (float)(rms * Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2));
        }
        return s;
    }

    public static double RmsDb(ReadOnlySpan<float> s)
    {
        double sum = 0;
        foreach (var v in s) sum += (double)v * v;
        return 20 * Math.Log10(Math.Sqrt(sum / Math.Max(1, s.Length)) + 1e-12);
    }

    /// <summary>Mixes white noise into <paramref name="speech"/> so that speech-RMS minus noise-RMS equals <paramref name="snrDb"/>.</summary>
    public static float[] AddNoise(float[] speech, double snrDb, int seed = 7)
    {
        double speechDb = RmsDb(speech);
        var noise = Noise(speech.Length / (double)Rate, speechDb - snrDb, seed);
        var o = new float[speech.Length];
        for (int i = 0; i < o.Length; i++) o[i] = speech[i] + noise[i];
        return o;
    }

    public static float[] Concat(params float[][] parts)
    {
        var o = new float[parts.Sum(p => p.Length)];
        int pos = 0;
        foreach (var p in parts) { p.CopyTo(o, pos); pos += p.Length; }
        return o;
    }

    /// <summary>Feeds audio into a segmenter in 20 ms frames (the tail is zero-padded to a whole frame).</summary>
    public static void Feed(Segmenter seg, float[] audio, bool gate = true, bool suppressed = false)
    {
        var frame = new float[Segmenter.FrameSamples];
        for (int i = 0; i < audio.Length; i += frame.Length)
        {
            Array.Clear(frame);
            Array.Copy(audio, i, frame, 0, Math.Min(frame.Length, audio.Length - i));
            seg.Push(frame, gate, suppressed);
        }
    }

    /// <summary>Normalizes for loose text comparison: lower case, letters and digits and single spaces only.</summary>
    public static string Norm(string s)
    {
        var sb = new StringBuilder();
        foreach (var c in s.ToLowerInvariant()) sb.Append(char.IsLetterOrDigit(c) ? c : ' ');
        return string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }
}

/// <summary>VAD driven by a script, so segmenter tests don't depend on audio content: frame index → speech?</summary>
internal sealed class ScriptedVad : IVad
{
    private readonly Func<int, bool> _speech;
    private int _frame;
    public ScriptedVad(Func<int, bool> speechAtFrame) => _speech = speechAtFrame;
    public bool IsSpeech { get; private set; }
    public void Push(ReadOnlySpan<float> frame) => IsSpeech = _speech(_frame++);
    public void Reset() { _frame = 0; IsSpeech = false; }

    public static int Frames(double seconds) => (int)Math.Round(seconds * 1000 / Segmenter.FrameMs);
}
