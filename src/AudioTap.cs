using System;
using System.Threading;
using Elements.Assets;
using FrooxEngine;

namespace SpeechToText;

/// <summary>
/// Hooks the default microphone (SPEC §3.1): subscribes to <c>AudioInput.NewFilteredSamples</c>, follows
/// <c>DefaultAudioInputChanged</c>, downmixes and resamples to 16 kHz mono and feeds the <see cref="AudioPipeline"/>.
/// The engine invokes the callback on its audio thread while holding the input's lock, so it does the minimum: no
/// allocation in steady state, no logging, never anything that can block.
/// </summary>
internal sealed class AudioTap : IDisposable
{
    private readonly Engine _engine;
    private readonly AudioPipeline _pipeline;
    private AudioInput? _input;
    private Resampler? _resampler;
    private float[] _mono = new float[4096];
    private float[] _out = new float[2048];
    private bool _disposed;
    private int _errorLogged;

    // Debug statistics, written by the audio thread, read racily by the controller's log line.
    private long _statSamples;
    private double _statSumSq;
    private float _statPeak;
    private long _statCallbacks;
    private int _statRate;

    public AudioTap(Engine engine, AudioPipeline pipeline)
    {
        _engine = engine;
        _pipeline = pipeline;
    }

    public string InputName => _input?.Name.ToString() ?? "(none)";
    public bool HasInput => _input != null;

    public void Start()
    {
        var audio = _engine.AudioSystem;
        audio.DefaultAudioInputChanged += OnDefaultChanged;
        Attach(audio.DefaultAudioInput);
    }

    private void OnDefaultChanged(AudioInput input) => Attach(input);

    private void Attach(AudioInput? input)
    {
        if (_disposed) return;
        Detach();
        _input = input;
        _pipeline.Reset(); // audio from the old device must not mix into the new one's utterance
        if (input == null) return;
        input.NewFilteredSamples += OnSamples;
        SpeechToTextMod.Msg($"[audio] listening on '{input.Name}' (engine rate {_engine.AudioSystem.SampleRate} Hz)");
    }

    private void Detach()
    {
        var input = _input;
        _input = null;
        if (input != null) input.NewFilteredSamples -= OnSamples;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _engine.AudioSystem.DefaultAudioInputChanged -= OnDefaultChanged; } catch { /* engine shutting down */ }
        Detach();
    }

    // ---- audio thread --------------------------------------------------------------------------------------------

    private void OnSamples(ReadOnlySpan<StereoSample> buffer)
    {
        try
        {
            if (_disposed) return;
            bool suppressed = Suppression.IsSuppressed(_engine, _input);
            _pipeline.Suppressed = suppressed;
            if (suppressed || buffer.Length == 0) return;

            int rate = _engine.AudioSystem.SampleRate;
            if (_resampler == null || _resampler.InputRate != rate) _resampler = new Resampler(rate, Segmenter.SampleRate);
            if (_mono.Length < buffer.Length) _mono = new float[buffer.Length * 2];

            double sumSq = 0;
            float peak = 0;
            for (int i = 0; i < buffer.Length; i++)
            {
                float m = (buffer[i].left + buffer[i].right) * 0.5f;
                _mono[i] = m;
                sumSq += (double)m * m;
                float a = m < 0 ? -m : m;
                if (a > peak) peak = a;
            }

            int max = _resampler.MaxOutput(buffer.Length);
            if (_out.Length < max) _out = new float[max * 2];
            int n = _resampler.Process(_mono.AsSpan(0, buffer.Length), _out);
            _pipeline.Write(_out.AsSpan(0, n));

            _statRate = rate;
            _statSumSq += sumSq;
            if (peak > _statPeak) _statPeak = peak;
            Interlocked.Add(ref _statSamples, buffer.Length);
            Interlocked.Increment(ref _statCallbacks);
        }
        catch (Exception e)
        {
            // Never throw into the engine's audio thread; tell the log once.
            if (Interlocked.Exchange(ref _errorLogged, 1) == 0) SpeechToTextMod.Error("[audio] callback failed: " + e);
        }
    }

    /// <summary>Debug: returns and resets the numbers collected since the last call.</summary>
    public (long Samples, long Callbacks, double RmsDb, double PeakDb, int Rate) TakeStats()
    {
        long samples = Interlocked.Exchange(ref _statSamples, 0);
        long callbacks = Interlocked.Exchange(ref _statCallbacks, 0);
        double sumSq = _statSumSq; _statSumSq = 0;
        float peak = _statPeak; _statPeak = 0;
        double rmsDb = samples > 0 ? 20 * Math.Log10(Math.Sqrt(sumSq / samples) + 1e-9) : double.NegativeInfinity;
        double peakDb = 20 * Math.Log10(peak + 1e-9);
        return (samples, callbacks, rmsDb, peakDb, _statRate);
    }
}

/// <summary>The mute / privacy rules of SPEC §3.2: whenever voice chat wouldn't transmit, we don't listen either.</summary>
internal static class Suppression
{
    public static bool IsSuppressed(Engine engine, AudioInput? input) => Reason(engine, input) != null;

    /// <summary>Why we must not listen right now (constant strings, no allocation), or null when listening is fine.</summary>
    public static string? Reason(Engine engine, AudioInput? input)
    {
        var audio = engine.AudioSystem;
        if (audio.IsMuted) return "muted";

        var world = engine.WorldManager?.FocusedWorld;
        var user = world?.LocalUser;
        if (world == null || user == null || world.IsUserspace()) return "no focused world"; // voice only goes out from the focused world
        if (user.IsSilenced) return "silenced by the host";
        if (user.ActiveVoiceMode == VoiceMode.Mute) return "voice mode is Mute";
        // Whisper is heard only nearby, but the text goes to everyone in the session: keep a whisper private.
        if (user.ActiveVoiceMode == VoiceMode.Whisper) return "whispering (the text would be visible to everyone)";
        if (ContactsDialog.RecordingVoiceMessage && input != null && input == audio.DefaultAudioInput) return "recording a voice message";
        return null;
    }
}
