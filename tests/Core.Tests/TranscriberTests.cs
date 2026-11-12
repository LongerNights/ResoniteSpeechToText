namespace SpeechToText.Tests;

public class TranscriberTests
{
    /// <summary>Engine whose output and speed the test controls. Text = "t&lt;sample count&gt;" unless overridden.</summary>
    private sealed class FakeEngine : ISpeechEngine
    {
        public readonly List<int> Calls = new();                   // sample counts, in the order they were transcribed
        public Func<float[], string> Text = s => "text " + s.Length;
        public Func<float[], Task>? Delay;
        public Func<float[], CancellationToken, Task>? DelayCt;    // like Delay, but honours cancellation
        public Func<float[], Exception?>? Fail;
        public readonly ManualResetEventSlim Started = new(false);
        public int Active, MaxActive;

        public async Task<Transcription> TranscribeAsync(float[] samples, TranscribeRequest request, CancellationToken ct = default)
        {
            int a = Interlocked.Increment(ref Active);
            lock (Calls) { Calls.Add(samples.Length); MaxActive = Math.Max(MaxActive, a); }
            Started.Set();
            try
            {
                if (Delay != null) await Delay(samples);
                if (DelayCt != null) await DelayCt(samples, ct);
                ct.ThrowIfCancellationRequested();
                var ex = Fail?.Invoke(samples);
                if (ex != null) throw ex;
                return new Transcription(Text(samples), "en", TimeSpan.FromMilliseconds(5));
            }
            finally { Interlocked.Decrement(ref Active); }
        }
        public void Dispose() { }
    }

    private static SegmentEvent Seg(SegmentEventKind kind, long id, int samples = 16000, int voicedMs = 2000) =>
        new() { Kind = kind, UtteranceId = id, Samples = new float[samples], VoicedMs = voicedMs };

    private sealed class Sink
    {
        public readonly List<TranscriptionEvent> Events = new();
        public readonly List<string> Warnings = new();
        public readonly List<Exception> Errors = new();
        public void On(TranscriptionEvent e) { lock (Events) Events.Add(e); }
        public TranscriptionEvent[] Snapshot() { lock (Events) return Events.ToArray(); }

        public bool WaitFor(int count, int ms = 5000)
        {
            var end = DateTime.UtcNow.AddMilliseconds(ms);
            while (DateTime.UtcNow < end) { if (Snapshot().Length >= count) return true; Thread.Sleep(5); }
            return false;
        }
    }

    private static Transcriber Make(FakeEngine engine, Sink sink, Func<TextFilter>? filter = null) =>
        new(engine, () => new TranscribeRequest("en"), filter ?? (() => new TextFilter()), sink.On, sink.Warnings.Add, sink.Errors.Add);

    [Fact]
    public void FinalsAreTranscribedInOrderOneAtATime()
    {
        var engine = new FakeEngine { Delay = _ => Task.Delay(20) };
        var sink = new Sink();
        using var t = Make(engine, sink);
        for (int i = 1; i <= 6; i++) t.Submit(Seg(SegmentEventKind.Final, i, samples: 16000 + i));

        Assert.True(sink.WaitFor(6));
        var events = sink.Snapshot();
        Assert.Equal(Enumerable.Range(1, 6).Select(i => (long)i), events.Select(e => e.UtteranceId));
        Assert.All(events, e => Assert.Equal(TranscriptionKind.Final, e.Kind));
        Assert.Equal(1, engine.MaxActive); // never two inferences at once
        Assert.Equal("text 16001", events[0].Text);
    }

    [Fact]
    public void FinalsAreNeverDroppedEvenWhenTheEngineIsSlowAndAWarningIsRaised()
    {
        var engine = new FakeEngine { Delay = _ => Task.Delay(30) };
        var sink = new Sink();
        using var t = Make(engine, sink);
        for (int i = 1; i <= 10; i++) t.Submit(Seg(SegmentEventKind.Final, i));
        Assert.True(sink.WaitFor(10));
        Assert.Equal(10, sink.Snapshot().Count(e => e.Kind == TranscriptionKind.Final));
        Assert.NotEmpty(sink.Warnings); // more than 4 waiting
    }

    [Fact]
    public void WhileBusyOnlyTheNewestPendingPartialSurvives()
    {
        var gate = new TaskCompletionSource();
        var engine = new FakeEngine { Delay = s => s.Length == 111 ? gate.Task : Task.CompletedTask };
        var sink = new Sink();
        using var t = Make(engine, sink);

        t.Submit(Seg(SegmentEventKind.Partial, 1, samples: 111)); // starts and blocks
        Assert.True(engine.Started.Wait(2000));
        t.Submit(Seg(SegmentEventKind.Partial, 1, samples: 222));
        t.Submit(Seg(SegmentEventKind.Partial, 1, samples: 333));
        t.Submit(Seg(SegmentEventKind.Partial, 1, samples: 444)); // 222 and 333 are replaced by this
        Assert.Equal(2, t.DroppedPartials);

        gate.SetResult();
        Assert.True(sink.WaitFor(2));
        Assert.Equal(new[] { 111, 444 }, engine.Calls);
        Assert.Equal(new[] { "text 111", "text 444" }, sink.Snapshot().Select(e => e.Text));
    }

    [Fact]
    public void ALaterFinalMakesAnEarlierPendingPartialStale()
    {
        var gate = new TaskCompletionSource();
        var engine = new FakeEngine { Delay = s => s.Length == 111 ? gate.Task : Task.CompletedTask };
        var sink = new Sink();
        using var t = Make(engine, sink);

        t.Submit(Seg(SegmentEventKind.Final, 1, samples: 111));   // blocks the engine
        Assert.True(engine.Started.Wait(2000));
        t.Submit(Seg(SegmentEventKind.Partial, 2, samples: 222)); // waiting for the engine
        t.Submit(Seg(SegmentEventKind.Final, 3, samples: 333));   // ids only grow, so a final for 3 means utterance 2 is over
        gate.SetResult();

        Assert.True(sink.WaitFor(2));
        Thread.Sleep(100);
        // The older partial is stale and was dropped; final 3 ran right after final 1.
        Assert.Equal(new[] { 111, 333 }, engine.Calls);
    }

    [Fact]
    public void AFinalOvertakesAPartialThatIsStillWaiting()
    {
        // Final 1 is running (slow), final 2 waits behind it, and utterance 3 is already being spoken and posts a partial.
        // When the engine frees up, both finals must go first and the partial last.
        var gate = new TaskCompletionSource();
        var engine = new FakeEngine { Delay = s => s.Length == 111 ? gate.Task : Task.CompletedTask };
        var sink = new Sink();
        using var t = Make(engine, sink);

        t.Submit(Seg(SegmentEventKind.Final, 1, samples: 111));   // running, blocked
        Assert.True(engine.Started.Wait(2000));
        t.Submit(Seg(SegmentEventKind.Final, 2, samples: 222));   // queued behind it
        t.Submit(Seg(SegmentEventKind.Partial, 3, samples: 333)); // utterance 3 in progress, partial waiting
        gate.SetResult();

        Assert.True(sink.WaitFor(3));
        Assert.Equal(new[] { 111, 222, 333 }, engine.Calls); // both finals first, the partial last
    }

    [Fact]
    public void AFinalCancelsTheStalePartialThatIsStillRunningSoItStartsAtOnce()
    {
        // A long partial for utterance 1 is mid-inference when utterance 1 ends. Its result would be discarded, so the engine must
        // be freed immediately for the final rather than making it wait for the partial to run to completion.
        var engine = new FakeEngine { DelayCt = (s, ct) => s.Length == 111 ? Task.Delay(Timeout.Infinite, ct) : Task.CompletedTask };
        var sink = new Sink();
        using var t = Make(engine, sink);

        t.Submit(Seg(SegmentEventKind.Partial, 1, samples: 111)); // would block forever unless cancelled
        Assert.True(engine.Started.Wait(2000));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        t.Submit(Seg(SegmentEventKind.Final, 1, samples: 555));

        Assert.True(sink.WaitFor(1, 3000), "the final never ran: the stale partial wasn't cancelled");
        Assert.True(sw.ElapsedMilliseconds < 1500, $"final took {sw.ElapsedMilliseconds} ms");
        var only = Assert.Single(sink.Snapshot());
        Assert.Equal(TranscriptionKind.Final, only.Kind);
        Assert.Equal("text 555", only.Text);
        Assert.Equal(new[] { 111, 555 }, engine.Calls);
        Assert.Empty(sink.Errors); // a deliberate cancel is not an error
    }

    [Fact]
    public void ADiscardedUtteranceAlsoCancelsItsRunningPartial()
    {
        var engine = new FakeEngine { DelayCt = (s, ct) => s.Length == 111 ? Task.Delay(Timeout.Infinite, ct) : Task.CompletedTask };
        var sink = new Sink();
        using var t = Make(engine, sink);

        t.Submit(Seg(SegmentEventKind.Partial, 1, samples: 111));
        Assert.True(engine.Started.Wait(2000));
        t.Submit(new SegmentEvent { Kind = SegmentEventKind.Discarded, UtteranceId = 1 });
        t.Submit(Seg(SegmentEventKind.Final, 2, samples: 777)); // the next utterance must not be stuck behind the dead partial

        Assert.True(sink.WaitFor(2, 3000));
        Assert.Equal(new[] { TranscriptionKind.Cleared, TranscriptionKind.Final }, sink.Snapshot().Select(e => e.Kind));
        Assert.Empty(sink.Errors);
    }

    [Fact]
    public void APartialOfALaterUtteranceIsNotCancelledByAnEarlierFinal()
    {
        // Final 1 arrives while a partial for utterance 2 is running: utterance 2 is still in progress, so its partial must survive.
        var release = new TaskCompletionSource();
        var engine = new FakeEngine { DelayCt = (s, ct) => s.Length == 222 ? release.Task.WaitAsync(ct) : Task.CompletedTask };
        var sink = new Sink();
        using var t = Make(engine, sink);

        t.Submit(Seg(SegmentEventKind.Partial, 2, samples: 222));
        Assert.True(engine.Started.Wait(2000));
        t.Submit(Seg(SegmentEventKind.Final, 1, samples: 111));
        Thread.Sleep(100);
        release.SetResult();

        Assert.True(sink.WaitFor(2, 3000));
        Assert.Contains(sink.Snapshot(), e => e.Kind == TranscriptionKind.Partial && e.UtteranceId == 2);
    }

    [Fact]
    public void ThePartialOfAnUtteranceThatAlreadyEndedIsNeverShown()
    {
        var gate = new TaskCompletionSource();
        var engine = new FakeEngine { Delay = s => s.Length == 111 ? gate.Task : Task.CompletedTask };
        var sink = new Sink();
        using var t = Make(engine, sink);

        t.Submit(Seg(SegmentEventKind.Partial, 1, samples: 111));      // in flight, slow
        Assert.True(engine.Started.Wait(2000));
        t.Submit(Seg(SegmentEventKind.Final, 1, samples: 555));        // the same utterance ends meanwhile
        gate.SetResult();

        Assert.True(sink.WaitFor(1));
        Thread.Sleep(100);
        var events = sink.Snapshot();
        Assert.Single(events);                                         // the stale partial was discarded
        Assert.Equal(TranscriptionKind.Final, events[0].Kind);
        Assert.Equal("text 555", events[0].Text);
    }

    [Fact]
    public void ALatePartialSubmittedAfterItsFinalIsIgnored()
    {
        var engine = new FakeEngine();
        var sink = new Sink();
        using var t = Make(engine, sink);
        t.Submit(Seg(SegmentEventKind.Final, 5));
        Assert.True(sink.WaitFor(1));
        t.Submit(Seg(SegmentEventKind.Partial, 5));
        t.Submit(Seg(SegmentEventKind.Partial, 4));
        Thread.Sleep(100);
        Assert.Single(sink.Snapshot());
        Assert.Single(engine.Calls);
    }

    [Fact]
    public void DiscardedUtterancesClearTheirPartialImmediately()
    {
        var gate = new TaskCompletionSource();
        var engine = new FakeEngine { Delay = s => s.Length == 111 ? gate.Task : Task.CompletedTask };
        var sink = new Sink();
        using var t = Make(engine, sink);

        t.Submit(Seg(SegmentEventKind.Partial, 1, samples: 111));
        Assert.True(engine.Started.Wait(2000));
        t.Submit(new SegmentEvent { Kind = SegmentEventKind.Discarded, UtteranceId = 1 });

        var cleared = Assert.Single(sink.Snapshot()); // emitted right away, not queued behind the busy engine
        Assert.Equal(TranscriptionKind.Cleared, cleared.Kind);

        gate.SetResult();
        Thread.Sleep(100);
        Assert.Single(sink.Snapshot()); // and the in-flight partial's result never shows up
    }

    [Fact]
    public void HallucinationsAreFilteredButStillReportedAsAnEmptyFinal()
    {
        var engine = new FakeEngine { Text = _ => "Thank you." };
        var sink = new Sink();
        using var t = Make(engine, sink);
        t.Submit(Seg(SegmentEventKind.Final, 1, voicedMs: 400));
        Assert.True(sink.WaitFor(1));
        var e = sink.Snapshot()[0];
        Assert.Equal(TranscriptionKind.Final, e.Kind);
        Assert.Equal("", e.Text); // the consumer needs the (empty) final to clear its partial
    }

    [Fact]
    public void EngineFailuresAreReportedAndTheWorkerKeepsGoing()
    {
        var engine = new FakeEngine { Fail = s => s.Length == 111 ? new InvalidOperationException("boom") : null };
        var sink = new Sink();
        using var t = Make(engine, sink);
        t.Submit(Seg(SegmentEventKind.Final, 1, samples: 111));
        t.Submit(Seg(SegmentEventKind.Final, 2, samples: 222));

        Assert.True(sink.WaitFor(2));
        var events = sink.Snapshot();
        Assert.Equal(TranscriptionKind.Cleared, events[0].Kind); // failed utterance: clear, don't leave stale partial text
        Assert.Equal("text 222", events[1].Text);
        Assert.Single(sink.Errors);
    }

    [Fact]
    public void ResultsCarryTimingAndMetadata()
    {
        var engine = new FakeEngine { Delay = _ => Task.Delay(40) };
        var sink = new Sink();
        using var t = Make(engine, sink);
        t.Submit(Seg(SegmentEventKind.Final, 9, samples: 32000, voicedMs: 1500));
        Assert.True(sink.WaitFor(1));
        var e = sink.Snapshot()[0];
        Assert.Equal(9, e.UtteranceId);
        Assert.Equal(2000, e.AudioMs);
        Assert.Equal(1500, e.VoicedMs);
        Assert.Equal("en", e.Language);
        Assert.True(e.SinceSubmitted >= TimeSpan.FromMilliseconds(20), $"SinceSubmitted was {e.SinceSubmitted}"); // 40 ms delay, 15 ms clock granularity
    }

    [Fact]
    public void DisposeStopsWorkAndSilencesFurtherEvents()
    {
        var gate = new TaskCompletionSource();
        var engine = new FakeEngine { Delay = _ => gate.Task };
        var sink = new Sink();
        var t = Make(engine, sink);
        t.Submit(Seg(SegmentEventKind.Final, 1));
        Assert.True(engine.Started.Wait(2000));
        t.Submit(Seg(SegmentEventKind.Final, 2));

        var disposing = Task.Run(t.Dispose);
        Assert.True(disposing.Wait(6000), "Dispose hung on a blocked engine");
        Assert.Empty(sink.Snapshot());
        t.Submit(Seg(SegmentEventKind.Final, 3)); // no-op after dispose
        Assert.Single(engine.Calls);
    }

    [Fact]
    public void SinkExceptionsDoNotKillTheWorker()
    {
        var engine = new FakeEngine();
        var errors = new List<Exception>();
        var seen = new List<long>();
        using var t = new Transcriber(engine, () => new TranscribeRequest("en"), () => new TextFilter(),
            e => { seen.Add(e.UtteranceId); if (e.UtteranceId == 1) throw new InvalidOperationException("sink"); }, onError: errors.Add);
        t.Submit(Seg(SegmentEventKind.Final, 1));
        t.Submit(Seg(SegmentEventKind.Final, 2));
        var end = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < end && seen.Count < 2) Thread.Sleep(5);
        Assert.Equal(new long[] { 1, 2 }, seen);
        Assert.Single(errors);
    }
}
