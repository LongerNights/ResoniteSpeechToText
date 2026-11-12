using System;

namespace SpeechToText;

/// <summary>Frame-based voice activity detector over 16 kHz mono audio.</summary>
public interface IVad
{
    /// <summary>Feed exactly <see cref="Segmenter.FrameSamples"/> samples (20 ms).</summary>
    void Push(ReadOnlySpan<float> frame);

    /// <summary>Whether the audio fed so far currently counts as speech.</summary>
    bool IsSpeech { get; }

    void Reset();
}

/// <summary>
/// Energy VAD (SPEC §6.4): speech when frame RMS exceeds the noise floor (10th percentile of the last 5 s) by
/// <c>sensitivityDb</c> for 3 consecutive frames, held for a 300 ms hangover. An absolute minimum level stops digital
/// silence and very faint hiss from ever counting as speech.
/// </summary>
public sealed class EnergyVad : IVad
{
    public const double MinAbsoluteDb = -50.0;
    private const int WindowFrames = 250;   // 5 s of 20 ms frames
    private const int StartFrames = 3;
    private const int HangoverFrames = 15;  // 300 ms
    private const double SeedFloorDb = -55.0;

    private readonly double _sensitivityDb;
    private readonly double[] _window = new double[WindowFrames];
    private readonly double[] _scratch = new double[WindowFrames];
    private int _head;
    private int _run;
    private int _hang;

    public EnergyVad(double sensitivityDb = 12.0)
    {
        _sensitivityDb = sensitivityDb;
        Reset();
    }

    public bool IsSpeech { get; private set; }
    public double NoiseFloorDb { get; private set; } = SeedFloorDb;

    public void Reset()
    {
        Array.Fill(_window, SeedFloorDb);
        _head = 0; _run = 0; _hang = 0; IsSpeech = false; NoiseFloorDb = SeedFloorDb;
    }

    public void Push(ReadOnlySpan<float> frame)
    {
        double sum = 0;
        foreach (float s in frame) sum += (double)s * s;
        double rms = Math.Sqrt(sum / Math.Max(1, frame.Length));
        double db = Math.Max(-100.0, 20.0 * Math.Log10(rms + 1e-9));

        double threshold = Math.Max(NoiseFloorDb + _sensitivityDb, MinAbsoluteDb);
        bool above = db > threshold;

        _window[_head] = db;
        _head = (_head + 1) % WindowFrames;
        Array.Copy(_window, _scratch, WindowFrames);
        Array.Sort(_scratch);
        NoiseFloorDb = _scratch[WindowFrames / 10];

        if (above)
        {
            _run++;
            if (_run >= StartFrames) { IsSpeech = true; _hang = HangoverFrames; }
            else if (IsSpeech) _hang = HangoverFrames;
        }
        else
        {
            _run = 0;
            if (IsSpeech && --_hang <= 0) IsSpeech = false;
        }
    }
}
