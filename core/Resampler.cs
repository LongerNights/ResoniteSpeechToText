using System;

namespace SpeechToText;

/// <summary>
/// Streaming mono sample-rate converter (windowed-sinc polyphase, rational ratio). Used to turn the engine's rate
/// (normally 48 kHz) into the 16 kHz Whisper needs. Keeps state between calls, so audio can be fed in arbitrary chunks.
/// </summary>
public sealed class Resampler
{
    private const int ZeroCrossings = 16; // sinc lobes on each side, measured at the lower of the two rates

    private readonly long _m; // input step per output, numerator (input rate / gcd)
    private readonly long _l; // denominator (output rate / gcd)
    private readonly int _half;
    private readonly float[][]? _taps; // [phase][tap]; null for pass-through
    private float[] _buf;
    private long _base;  // absolute input index of _buf[0]
    private int _count;
    private long _next;  // index of the next output sample

    public int InputRate { get; }
    public int OutputRate { get; }

    public Resampler(int inputRate, int outputRate)
    {
        if (inputRate <= 0 || outputRate <= 0) throw new ArgumentOutOfRangeException();
        InputRate = inputRate;
        OutputRate = outputRate;
        long g = Gcd(inputRate, outputRate);
        _m = inputRate / g;
        _l = outputRate / g;
        if (_m == _l) { _half = 1; _buf = Array.Empty<float>(); return; }
        if (_l > 20000) throw new ArgumentException($"Unsupported rate pair {inputRate}->{outputRate}");

        double ratio = (double)_m / _l;                  // > 1 when downsampling
        _half = (int)Math.Ceiling(ZeroCrossings * Math.Max(1.0, ratio));
        double fc = 0.5 * Math.Min(1.0, 1.0 / ratio) * 0.92; // cycles per input sample, with a transition-band margin
        int taps = 2 * _half;
        _taps = new float[_l][];
        for (int p = 0; p < _l; p++)
        {
            double f = (double)p / _l;
            var row = new float[taps];
            double sum = 0;
            for (int k = 0; k < taps; k++)
            {
                double x = (k - _half + 1) - f;
                double sinc = x == 0 ? 1.0 : Math.Sin(2 * Math.PI * fc * x) / (2 * Math.PI * fc * x);
                double w = 0.42 + 0.5 * Math.Cos(Math.PI * x / _half) + 0.08 * Math.Cos(2 * Math.PI * x / _half);
                double h = 2 * fc * sinc * w;
                row[k] = (float)h;
                sum += h;
            }
            for (int k = 0; k < taps; k++) row[k] = (float)(row[k] / sum);
            _taps[p] = row;
        }

        _buf = new float[4096 + taps];
        _base = -(_half - 1);          // virtual zeros before the first sample
        _count = _half - 1;
    }

    /// <summary>Upper bound on output samples for <paramref name="inputCount"/> input samples.</summary>
    public int MaxOutput(int inputCount) => (int)((long)inputCount * _l / _m) + 2;

    /// <summary>Converts <paramref name="input"/>; returns the number of samples written to <paramref name="output"/>.</summary>
    public int Process(ReadOnlySpan<float> input, Span<float> output)
    {
        if (_taps == null)
        {
            if (output.Length < input.Length) throw new ArgumentException("output too small");
            input.CopyTo(output);
            return input.Length;
        }
        if (output.Length < MaxOutput(input.Length)) throw new ArgumentException("output too small (use MaxOutput)");

        if (_count + input.Length > _buf.Length)
            Array.Resize(ref _buf, Math.Max(_buf.Length * 2, _count + input.Length));
        input.CopyTo(_buf.AsSpan(_count));
        _count += input.Length;

        int o = 0;
        long lastAvail = _base + _count - 1;
        int taps = 2 * _half;
        while (true)
        {
            long pos = _next * _m;
            long i = pos / _l;
            if (i + _half > lastAvail) break;
            float[] row = _taps[(int)(pos % _l)];
            ReadOnlySpan<float> src = _buf.AsSpan((int)(i - _half + 1 - _base), taps);
            float acc = 0;
            for (int k = 0; k < taps; k++) acc += row[k] * src[k];
            output[o++] = acc;
            _next++;
        }

        // Drop history we no longer need.
        long keepFrom = (_next * _m) / _l - _half + 1;
        int drop = (int)(keepFrom - _base);
        if (drop > 0)
        {
            Array.Copy(_buf, drop, _buf, 0, _count - drop);
            _count -= drop;
            _base += drop;
        }
        return o;
    }

    private static long Gcd(long a, long b) { while (b != 0) { (a, b) = (b, a % b); } return a; }
}
