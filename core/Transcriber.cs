using System;
using System.Collections.Generic;
using System.Threading;

namespace SpeechToText;

public enum TranscriptionKind
{
    /// <summary>Hypothesis for an utterance still in progress.</summary>
    Partial,
    /// <summary>Finished utterance (the text may be empty after filtering, e.g. a hallucination on a breath).</summary>
    Final,
    /// <summary>The utterance was thrown away: clear any partial text that was showing.</summary>
    Cleared,
}

/// <param name="Inference">Time spent inside the speech engine.</param>
/// <param name="SinceSubmitted">Time from the segment event reaching the transcriber until this result (queue wait + inference + filtering).</param>
public sealed record TranscriptionEvent(
    TranscriptionKind Kind, long UtteranceId, string Text, string Language,
    int AudioMs, int VoicedMs, TimeSpan Inference, TimeSpan SinceSubmitted);

/// <summary>
/// Runs speech recognition for the segmenter's events on one background thread (SPEC §6.1):
/// <list type="bullet">
/// <item>Finals are never dropped and come out in order.</item>
/// <item>Partials never delay a final. While the engine is busy only the newest pending partial is kept (older ones are
/// replaced and counted in <see cref="DroppedPartials"/>), so a busy engine shows fresh text rather than stale text.</item>
/// <item>Once an utterance has ended, its late partial results are discarded instead of overwriting the final text.</item>
/// </list>
/// </summary>
public sealed class Transcriber : IDisposable
{
    private const int FinalQueueWarnDepth = 4;

    private readonly ISpeechEngine _engine;
    private readonly Func<TranscribeRequest> _request;
    private readonly Func<TextFilter> _filter;
    private readonly Action<TranscriptionEvent> _onEvent;
    private readonly Action<string>? _warn;
    private readonly Action<Exception>? _onError;
    private readonly Func<long> _clockMs;

    private readonly object _lock = new();
    private readonly Queue<(SegmentEvent Event, long At)> _finals = new();
    private (SegmentEvent Event, long At)? _pendingPartial;
    private long _endedUpTo; // utterance ids <= this have finished (final or discarded): their partials are stale
    private CancellationTokenSource? _jobCts; // the job being processed right now, so a stale partial can be cancelled
    private long _jobUtterance;
    private bool _jobIsPartial;
    private long _droppedPartials;
    private bool _disposed;
    private readonly CancellationTokenSource _cts = new();
    private readonly Thread _thread;

    public Transcriber(ISpeechEngine engine, Func<TranscribeRequest> request, Func<TextFilter> filter,
        Action<TranscriptionEvent> onEvent, Action<string>? warn = null, Action<Exception>? onError = null, Func<long>? clockMs = null)
    {
        _engine = engine;
        _request = request;
        _filter = filter;
        _onEvent = onEvent;
        _warn = warn;
        _onError = onError;
        _clockMs = clockMs ?? (() => Environment.TickCount64);
        _thread = new Thread(Run) { IsBackground = true, Name = "SpeechToText recognizer", Priority = ThreadPriority.BelowNormal };
        _thread.Start();
    }

    public int QueuedFinals { get { lock (_lock) return _finals.Count; } }
    public long DroppedPartials => Interlocked.Read(ref _droppedPartials);

    /// <summary>Hand over a segmenter event. Cheap and non-blocking; safe to call from the audio worker.</summary>
    public void Submit(SegmentEvent e)
    {
        long now = _clockMs();
        bool clear = false;
        CancellationTokenSource? cancelRunningPartial = null;
        lock (_lock)
        {
            if (_disposed) return;
            switch (e.Kind)
            {
                case SegmentEventKind.Partial:
                    if (e.UtteranceId <= _endedUpTo) return; // already finished: nothing to show
                    if (_pendingPartial != null) Interlocked.Increment(ref _droppedPartials);
                    _pendingPartial = (e, now);
                    break;

                case SegmentEventKind.Final:
                    _endedUpTo = Math.Max(_endedUpTo, e.UtteranceId);
                    if (_pendingPartial is { } p && p.Event.UtteranceId <= _endedUpTo) _pendingPartial = null;
                    if (_jobCts != null && _jobIsPartial && _jobUtterance <= _endedUpTo) cancelRunningPartial = _jobCts;
                    _finals.Enqueue((e, now));
                    if (_finals.Count > FinalQueueWarnDepth)
                        _warn?.Invoke($"{_finals.Count} utterances are waiting for recognition: the speech engine can't keep up");
                    break;

                case SegmentEventKind.Discarded:
                    _endedUpTo = Math.Max(_endedUpTo, e.UtteranceId);
                    if (_pendingPartial is { } q && q.Event.UtteranceId <= _endedUpTo) _pendingPartial = null;
                    if (_jobCts != null && _jobIsPartial && _jobUtterance <= _endedUpTo) cancelRunningPartial = _jobCts;
                    clear = true;
                    break;
            }
            Monitor.PulseAll(_lock);
        }
        // Its result would be thrown away anyway, so free the engine for the final right now instead of letting it finish.
        try { cancelRunningPartial?.Cancel(); } catch (ObjectDisposedException) { /* the job just ended */ }
        if (clear) Emit(new TranscriptionEvent(TranscriptionKind.Cleared, e.UtteranceId, "", "", 0, e.VoicedMs, TimeSpan.Zero, TimeSpan.Zero));
    }

    private void Run()
    {
        while (true)
        {
            (SegmentEvent Event, long At) job;
            bool isFinal;
            CancellationTokenSource jobCts;
            lock (_lock)
            {
                while (!_disposed && _finals.Count == 0 && _pendingPartial == null) Monitor.Wait(_lock);
                if (_disposed) return;
                if (_finals.Count > 0) { job = _finals.Dequeue(); isFinal = true; }
                else { job = _pendingPartial!.Value; _pendingPartial = null; isFinal = false; }
                jobCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                _jobCts = jobCts; _jobUtterance = job.Event.UtteranceId; _jobIsPartial = !isFinal;
            }

            try { Process(job.Event, job.At, isFinal, jobCts.Token); }
            catch (OperationCanceledException)
            {
                if (_cts.IsCancellationRequested) return; // shutting down
                // otherwise: a stale partial was cancelled on purpose, nothing to report
            }
            catch (Exception e)
            {
                _onError?.Invoke(e);
                if (isFinal) Emit(new TranscriptionEvent(TranscriptionKind.Cleared, job.Event.UtteranceId, "", "", 0, 0, TimeSpan.Zero, TimeSpan.Zero));
            }
            finally
            {
                lock (_lock) _jobCts = null;
                jobCts.Dispose();
            }
        }
    }

    private void Process(SegmentEvent e, long submittedAt, bool isFinal, CancellationToken ct)
    {
        var result = _engine.TranscribeAsync(e.Samples, _request(), ct).GetAwaiter().GetResult();
        if (result.TimedOut) _warn?.Invoke($"recognition of a {e.DurationMs / 1000.0:F1} s clip was stopped after {result.Elapsed.TotalSeconds:F1} s (runaway decode)");

        if (!isFinal)
        {
            lock (_lock) if (e.UtteranceId <= _endedUpTo || _disposed) return; // the utterance ended while we were working
        }

        string text = _filter().Apply(result.Text, e.VoicedMs);
        Emit(new TranscriptionEvent(isFinal ? TranscriptionKind.Final : TranscriptionKind.Partial, e.UtteranceId, text, result.Language,
            e.DurationMs, e.VoicedMs, result.Elapsed, TimeSpan.FromMilliseconds(_clockMs() - submittedAt)));
    }

    private void Emit(TranscriptionEvent e)
    {
        try { _onEvent(e); }
        catch (Exception ex) { _onError?.Invoke(ex); }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            _finals.Clear();
            _pendingPartial = null;
            Monitor.PulseAll(_lock);
        }
        _cts.Cancel();
        _thread.Join(TimeSpan.FromSeconds(5));
        _cts.Dispose();
    }
}
