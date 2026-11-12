using System;
using System.Linq;
using Whisper.net;

namespace SpeechToText;

/// <summary>
/// Silero VAD through Whisper.net. The native detector works on whole buffers, so this keeps a sliding window of recent
/// audio and re-runs it every <see cref="EvalEveryFrames"/> frames; speech is "a detected segment reaches the end of
/// the window". The decision therefore lags real time by up to ~100 ms plus the detector's own minimum durations, which
/// the segmenter's pre-roll absorbs.
/// </summary>
public sealed class SileroVad : IVad, IDisposable
{
    public const int WindowMs = 1000;
    public const int EvalEveryFrames = 5; // 100 ms

    private readonly WhisperVadFactory _factory;
    private readonly WhisperVadProcessor _processor;
    private readonly float[] _window = new float[Segmenter.SampleRate * WindowMs / 1000];
    private int _filled;
    private int _frames;

    public SileroVad(string modelPath, float threshold = 0.5f)
    {
        _factory = WhisperVadFactory.FromPath(modelPath);
        _processor = _factory.CreateBuilder()
            .WithThreshold(threshold)
            .WithMinSpeechDuration(TimeSpan.FromMilliseconds(100))
            .WithMinSilenceDuration(TimeSpan.FromMilliseconds(100))
            .WithSpeechPadding(TimeSpan.Zero)
            .WithThreads(1)
            .Build();
    }

    public bool IsSpeech { get; private set; }

    public void Reset()
    {
        _filled = 0; _frames = 0; IsSpeech = false;
        _processor.ResetState();
    }

    public void Push(ReadOnlySpan<float> frame)
    {
        // Slide the window left by one frame and append.
        if (_filled + frame.Length > _window.Length)
        {
            int keep = _window.Length - frame.Length;
            Array.Copy(_window, _filled - keep, _window, 0, keep);
            _filled = keep;
        }
        frame.CopyTo(_window.AsSpan(_filled));
        _filled += frame.Length;

        if (++_frames % EvalEveryFrames != 0) return;

        var windowDuration = TimeSpan.FromSeconds((double)_filled / Segmenter.SampleRate);
        TimeSpan lastEnd = TimeSpan.Zero;
        foreach (var seg in _processor.DetectSpeech(_window.AsSpan(0, _filled)))
            if (seg.End > lastEnd) lastEnd = seg.End;
        IsSpeech = lastEnd > TimeSpan.Zero && windowDuration - lastEnd <= TimeSpan.FromMilliseconds(80);
    }

    public void Dispose()
    {
        _processor.Dispose();
        _factory.Dispose();
    }
}
