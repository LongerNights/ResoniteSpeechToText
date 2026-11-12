using System;

namespace SpeechToText;

public sealed record SegmenterOptions
{
    public SttMode Mode { get; init; } = SttMode.Continuous;
    public int PreRollMs { get; init; } = 300;
    public int PostRollMs { get; init; } = 200;
    public int SilenceMs { get; init; } = 700;
    public int MinSpeechMs { get; init; } = 300;
    public int MaxSegmentSec { get; init; } = 15;
    public int PartialIntervalMs { get; init; } = 1000;
    public bool PublishPartials { get; init; } = true;
}

public enum SegmentEventKind
{
    /// <summary>Hypothesis request for the utterance so far; the utterance is still in progress.</summary>
    Partial,
    /// <summary>The utterance ended: transcribe <see cref="SegmentEvent.Samples"/>.</summary>
    Final,
    /// <summary>The in-progress utterance was thrown away (muted, too short, ...). Clear any partial text.</summary>
    Discarded,
}

public sealed class SegmentEvent
{
    public SegmentEventKind Kind { get; init; }
    /// <summary>Which utterance this belongs to: increases by one each time a new segment begins. Lets consumers drop stale partials.</summary>
    public long UtteranceId { get; init; }
    /// <summary>16 kHz mono audio (empty for <see cref="SegmentEventKind.Discarded"/>).</summary>
    public float[] Samples { get; init; } = Array.Empty<float>();
    /// <summary>Milliseconds of audio the VAD classified as speech.</summary>
    public int VoicedMs { get; init; }
    public int DurationMs => Samples.Length * 1000 / Segmenter.SampleRate;
}

/// <summary>
/// Turns a stream of 20 ms frames plus an activation gate into utterances (SPEC §6.1). Deterministic: time is counted in
/// frames, never read from a clock, so it can be tested with synthetic audio.
/// <list type="bullet">
/// <item>Continuous: an utterance starts when the VAD fires (with pre-roll) and ends after <c>SilenceMs</c> of silence.</item>
/// <item>Triggered: an utterance starts when the gate opens (with pre-roll) and ends <c>PostRollMs</c> after it closes.</item>
/// </list>
/// </summary>
public sealed class Segmenter
{
    public const int SampleRate = 16000;
    public const int FrameMs = 20;
    public const int FrameSamples = SampleRate * FrameMs / 1000; // 320

    private readonly SegmenterOptions _o;
    private readonly IVad _vad;
    private readonly Action<SegmentEvent> _sink;

    private readonly float[] _pre;      // pre-roll ring, in whole frames
    private readonly int _preFrames;
    private int _preHead, _preCount;

    private float[] _seg = new float[SampleRate * 4];
    private int _segFrames;
    private bool _inSegment;
    private int _voiced;                // voiced frames in the current segment
    private int _silence;               // trailing non-speech frames (Continuous)
    private int _sincePartial;
    private int _closing = -1;          // frames of post-roll left after the gate closed (Triggered), -1 = gate open
    private bool _gatePrev;
    private bool _forceRestart;         // continue straight into a new segment after a max-length split
    private long _utteranceCounter;
    private long _utteranceId;

    public Segmenter(SegmenterOptions options, IVad vad, Action<SegmentEvent> sink)
    {
        _o = options;
        _vad = vad;
        _sink = sink;
        _preFrames = Math.Max(0, options.PreRollMs / FrameMs);
        _pre = new float[Math.Max(1, _preFrames) * FrameSamples];
    }

    /// <summary>A segment is being recorded.</summary>
    public bool InSegment => _inSegment;

    /// <summary>Speech is being detected inside an open gate.</summary>
    public bool Speaking { get; private set; }

    /// <summary>
    /// Feed one 20 ms frame. <paramref name="gateOpen"/> is the activation state (always true for Continuous unless the
    /// caller wants to pause); <paramref name="suppressed"/> means muted / silenced / unfocused: drop everything.
    /// </summary>
    public void Push(ReadOnlySpan<float> frame, bool gateOpen, bool suppressed)
    {
        if (frame.Length != FrameSamples) throw new ArgumentException($"frame must be {FrameSamples} samples");

        _vad.Push(frame);
        bool speech = _vad.IsSpeech;

        if (suppressed)
        {
            if (_inSegment) EndSegment(discard: true);
            ClearPreRoll();
            Speaking = false;
            _gatePrev = false;
            _forceRestart = false;
            return;
        }

        Speaking = speech && gateOpen;

        if (!_inSegment)
        {
            bool start = _o.Mode == SttMode.Triggered
                ? gateOpen && (!_gatePrev || _forceRestart)
                : gateOpen && (speech || _forceRestart);
            if (start)
            {
                bool usePre = !_forceRestart;
                _forceRestart = false;
                BeginSegment(frame, speech, usePre);
            }
            else AddPreRoll(frame);
            _gatePrev = gateOpen;
            return;
        }

        // In a segment. Continuous mode ends the moment the gate closes, without taking that frame; Triggered mode keeps
        // recording for PostRollMs after the release (handled below).
        if (_o.Mode != SttMode.Triggered && !gateOpen)
        {
            EndSegment(discard: false);
            _gatePrev = false;
            return;
        }
        Append(frame);
        if (speech) _voiced++;
        _sincePartial++;

        if (_o.Mode == SttMode.Triggered)
        {
            if (!gateOpen)
            {
                int post = Math.Max(0, _o.PostRollMs / FrameMs);
                if (_closing < 0) _closing = post;
                else _closing--;
                if (_closing <= 0) { EndSegment(discard: false); _gatePrev = gateOpen; return; }
            }
            else _closing = -1;
        }
        else
        {
            _silence = speech ? 0 : _silence + 1;
            if (_silence * FrameMs >= _o.SilenceMs) { EndSegment(discard: false); _gatePrev = gateOpen; return; }
        }

        if (_segFrames * FrameMs >= _o.MaxSegmentSec * 1000)
        {
            EndSegment(discard: false);
            _forceRestart = gateOpen;
            _gatePrev = gateOpen;
            return;
        }

        if (_o.PublishPartials && (_o.Mode == SttMode.Triggered || speech)
            && _sincePartial * FrameMs >= _o.PartialIntervalMs && _voiced * FrameMs >= _o.MinSpeechMs)
        {
            _sincePartial = 0;
            _sink(new SegmentEvent { Kind = SegmentEventKind.Partial, UtteranceId = _utteranceId, Samples = Snapshot(_segFrames), VoicedMs = _voiced * FrameMs });
        }
        _gatePrev = gateOpen;
    }

    /// <summary>Drop any in-progress segment without emitting anything (e.g. mode switched).</summary>
    public void Reset()
    {
        _inSegment = false; _segFrames = 0; _voiced = 0; _silence = 0; _sincePartial = 0; _closing = -1;
        _gatePrev = false; _forceRestart = false; Speaking = false;
        ClearPreRoll();
        _vad.Reset();
    }

    // ---- internals -------------------------------------------------------------------------------------------------

    private void BeginSegment(ReadOnlySpan<float> frame, bool speech, bool usePreRoll)
    {
        _inSegment = true;
        _utteranceId = ++_utteranceCounter;
        _segFrames = 0; _voiced = 0; _silence = 0; _sincePartial = 0; _closing = -1;
        if (usePreRoll)
        {
            int start = (_preHead - _preCount + _preFrames * 2) % Math.Max(1, _preFrames);
            for (int i = 0; i < _preCount; i++)
                Append(_pre.AsSpan(((start + i) % _preFrames) * FrameSamples, FrameSamples));
        }
        ClearPreRoll();
        Append(frame);
        if (speech) _voiced++;
    }

    private void EndSegment(bool discard)
    {
        int frames = _segFrames;
        int voicedMs = _voiced * FrameMs;
        if (!discard && _o.Mode != SttMode.Triggered)
        {
            // Keep only PostRollMs of the trailing silence that ended the utterance.
            int keepSilence = Math.Max(0, _o.PostRollMs / FrameMs);
            frames -= Math.Max(0, _silence - keepSilence);
        }
        bool tooShort = voicedMs < _o.MinSpeechMs;
        var samples = Snapshot(frames);
        long id = _utteranceId;

        _inSegment = false; _segFrames = 0; _voiced = 0; _silence = 0; _sincePartial = 0; _closing = -1;
        ClearPreRoll();

        if (discard || tooShort)
            _sink(new SegmentEvent { Kind = SegmentEventKind.Discarded, UtteranceId = id, VoicedMs = voicedMs });
        else
            _sink(new SegmentEvent { Kind = SegmentEventKind.Final, UtteranceId = id, Samples = samples, VoicedMs = voicedMs });
    }

    private void Append(ReadOnlySpan<float> frame)
    {
        int need = (_segFrames + 1) * FrameSamples;
        if (need > _seg.Length) Array.Resize(ref _seg, Math.Max(_seg.Length * 2, need));
        frame.CopyTo(_seg.AsSpan(_segFrames * FrameSamples));
        _segFrames++;
    }

    private float[] Snapshot(int frames) => _seg.AsSpan(0, frames * FrameSamples).ToArray();

    private void AddPreRoll(ReadOnlySpan<float> frame)
    {
        if (_preFrames == 0) return;
        frame.CopyTo(_pre.AsSpan(_preHead * FrameSamples, FrameSamples));
        _preHead = (_preHead + 1) % _preFrames;
        if (_preCount < _preFrames) _preCount++;
    }

    private void ClearPreRoll() { _preHead = 0; _preCount = 0; }
}
