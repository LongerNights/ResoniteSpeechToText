using System;
using System.Threading;

namespace SpeechToText;

/// <summary>
/// The engine-independent half of the audio path (SPEC §4): 16 kHz mono audio goes in through <see cref="Write"/> (from
/// the audio thread: lock-free, allocation-free), a worker thread cuts it into 20 ms frames, runs them through the VAD
/// and <see cref="Segmenter"/>, and hands <see cref="SegmentEvent"/>s to the sink.
/// <para>
/// "Suppressed" (muted / silenced / unfocused) and "starved" (no audio arriving: muted by the engine, mic unplugged)
/// both discard the utterance in progress, so audio from before and after a gap is never glued together.
/// </para>
/// </summary>
public sealed class AudioPipeline : IDisposable
{
    private const int StarveAfterMs = 500;

    private readonly RingBuffer _ring;
    private readonly Segmenter _segmenter;
    private readonly IVad _vad;
    private readonly Action<bool, bool>? _onState;
    private readonly Action<Exception>? _onError;
    private readonly Func<long> _clockMs;

    private readonly float[] _scratch = new float[4096];
    private readonly float[] _frame = new float[Segmenter.FrameSamples];
    private int _frameFill;

    private volatile bool _gate;
    private volatile bool _suppressed;
    private long _lastWriteMs;
    private bool _suppressionApplied;   // worker-only: a suppressed frame has already been pushed for the current gap
    private bool _hadAudioSinceGap = true;
    private (bool Listening, bool Speaking)? _lastState;
    private long _framesProcessed;

    private Thread? _thread;
    private volatile bool _stop;

    /// <param name="onState">Called from the worker when (listening, speaking) changes.</param>
    /// <param name="clockMs">Test seam; defaults to a monotonic millisecond clock.</param>
    public AudioPipeline(IVad vad, SegmenterOptions options, Action<SegmentEvent> sink,
        Action<bool, bool>? onState = null, Action<Exception>? onError = null, Func<long>? clockMs = null, int ringSeconds = 60)
    {
        _vad = vad;
        _onState = onState;
        _onError = onError;
        _clockMs = clockMs ?? (() => Environment.TickCount64);
        _ring = new RingBuffer(Segmenter.SampleRate * ringSeconds);
        _segmenter = new Segmenter(options, vad, sink);
        _gate = options.Mode == SttMode.Continuous; // triggered mode starts closed until the trigger variable opens it
        _lastWriteMs = _clockMs();
    }

    /// <summary>Activation gate. Continuous mode defaults to open, triggered mode to closed.</summary>
    public bool Gate { get => _gate; set => _gate = value; }

    /// <summary>Muted / silenced / unfocused: audio is dropped and the utterance in progress is discarded.</summary>
    public bool Suppressed { get => _suppressed; set => _suppressed = value; }

    /// <summary>Audio is being captured for transcription right now.</summary>
    public bool Listening => _gate && !_suppressed;

    public bool Speaking => _segmenter.Speaking;
    public bool InSegment => _segmenter.InSegment;
    public long DroppedSamples => _ring.DroppedSamples;
    public long FramesProcessed => Interlocked.Read(ref _framesProcessed);

    /// <summary>Audio thread. 16 kHz mono floats in [-1, 1].</summary>
    public void Write(ReadOnlySpan<float> mono16k)
    {
        _ring.Write(mono16k);
        Volatile.Write(ref _lastWriteMs, _clockMs());
    }

    /// <summary>
    /// Worker side: consume everything buffered, in whole frames. Returns the number of frames pushed to the segmenter.
    /// Must be called from a single thread (the worker, or a test).
    /// </summary>
    public int ProcessAvailable()
    {
        int frames = 0;
        bool suppressed = _suppressed;

        if (suppressed)
        {
            _ring.Clear();
            _frameFill = 0;
            if (!_suppressionApplied) ApplySuppression();
        }
        else
        {
            if (_suppressionApplied) { _suppressionApplied = false; } // audio flows again; gate/VAD state was reset when the gap began

            int n;
            while ((n = _ring.Read(_scratch)) > 0)
            {
                _hadAudioSinceGap = true;
                for (int i = 0; i < n;)
                {
                    int take = Math.Min(n - i, _frame.Length - _frameFill);
                    Array.Copy(_scratch, i, _frame, _frameFill, take);
                    _frameFill += take;
                    i += take;
                    if (_frameFill == _frame.Length)
                    {
                        _frameFill = 0;
                        _segmenter.Push(_frame, _gate, suppressed: false);
                        frames++;
                    }
                }
                if (_suppressed) break; // flipped while draining; handled next call
            }

            // No audio for a while (engine muted the mic, device gone): an utterance in progress can't be continued.
            if (frames == 0 && _hadAudioSinceGap && _segmenter.InSegment
                && _clockMs() - Volatile.Read(ref _lastWriteMs) > StarveAfterMs)
            {
                _hadAudioSinceGap = false;
                ApplySuppression(keepSuppressedFlag: false);
            }
        }

        Interlocked.Add(ref _framesProcessed, frames);
        ReportState();
        return frames;
    }

    /// <summary>Drop buffered audio and any utterance in progress (device switched, mode changed).</summary>
    public void Reset()
    {
        _ring.Clear();
        _frameFill = 0;
        _segmenter.Reset();
        _suppressionApplied = false;
        _hadAudioSinceGap = true;
    }

    public void Start()
    {
        if (_thread != null) return;
        _stop = false;
        _thread = new Thread(Run) { IsBackground = true, Name = "SpeechToText worker", Priority = ThreadPriority.BelowNormal };
        _thread.Start();
    }

    public void Dispose()
    {
        _stop = true;
        _thread?.Join(TimeSpan.FromSeconds(3));
        _thread = null;
    }

    private void Run()
    {
        while (!_stop)
        {
            try { if (ProcessAvailable() == 0) Thread.Sleep(5); }
            catch (Exception e) { _onError?.Invoke(e); Thread.Sleep(250); }
        }
    }

    private void ApplySuppression(bool keepSuppressedFlag = true)
    {
        // One suppressed frame is enough: the segmenter discards the utterance and pre-roll, and the VAD forgets its window.
        _segmenter.Push(_silentFrame, _gate, suppressed: true);
        _vad.Reset();
        if (keepSuppressedFlag) _suppressionApplied = true;
    }

    private static readonly float[] _silentFrame = new float[Segmenter.FrameSamples];

    private void ReportState()
    {
        var state = (Listening, Speaking);
        if (_lastState == state) return;
        _lastState = state;
        try { _onState?.Invoke(state.Item1, state.Item2); }
        catch (Exception e) { _onError?.Invoke(e); }
    }
}
