namespace SpeechToText.Tests;

public class AudioPipelineTests
{
    private sealed class Harness
    {
        public readonly List<SegmentEvent> Events = new();
        public readonly List<(bool Listening, bool Speaking)> States = new();
        public long Now;
        public readonly AudioPipeline Pipeline;

        public Harness(SttMode mode = SttMode.Continuous, IVad? vad = null, bool partials = false)
        {
            Pipeline = new AudioPipeline(vad ?? new EnergyVad(),
                new SegmenterOptions { Mode = mode, PublishPartials = partials },
                Events.Add, (l, s) => States.Add((l, s)), clockMs: () => Now);
        }

        /// <summary>Writes audio in odd-sized chunks, processing after each, advancing the fake clock in real time.</summary>
        public void Feed(float[] audio, int chunk = 777)
        {
            for (int i = 0; i < audio.Length; i += chunk)
            {
                int n = Math.Min(chunk, audio.Length - i);
                Pipeline.Write(audio.AsSpan(i, n));
                Now += n * 1000L / Audio.Rate;
                Pipeline.ProcessAvailable();
            }
        }
    }

    private static float[] SpeechLike(double seconds) => Audio.Sine(300, seconds, 0.3);

    [Fact]
    public void ContinuousUtteranceComesOutWhateverTheChunking()
    {
        var audio = Audio.Concat(Audio.Silence(1), SpeechLike(1.5), Audio.Silence(2));
        var lengths = new List<int>();
        foreach (int chunk in new[] { 1, 137, 320, 777, 4000 })
        {
            var h = new Harness();
            h.Feed(audio, chunk);
            var final = Assert.Single(h.Events, e => e.Kind == SegmentEventKind.Final);
            lengths.Add(final.Samples.Length);
        }
        Assert.Single(lengths.Distinct()); // frame-based, so chunk size can't matter
        Assert.InRange(lengths[0] / (double)Audio.Rate, 1.8, 2.3); // 300 ms pre-roll + 1.5 s + <= 200 ms post-roll, ± VAD onset
    }

    [Fact]
    public void FramesStraddlingWritesAreAssembledCorrectly()
    {
        var h = new Harness();
        h.Pipeline.Write(new float[100]);
        Assert.Equal(0, h.Pipeline.ProcessAvailable());     // less than a frame: nothing yet
        h.Pipeline.Write(new float[220]);
        Assert.Equal(1, h.Pipeline.ProcessAvailable());     // 100 + 220 = one whole frame
        h.Pipeline.Write(new float[320 * 3 + 5]);
        Assert.Equal(3, h.Pipeline.ProcessAvailable());     // 5 samples are left over, waiting for the next write
        h.Pipeline.Write(new float[314]);
        Assert.Equal(0, h.Pipeline.ProcessAvailable());     // 5 + 314 = 319: one short
        h.Pipeline.Write(new float[1]);
        Assert.Equal(1, h.Pipeline.ProcessAvailable());     // now it completes
    }

    [Fact]
    public void SuppressionDiscardsTheUtteranceInProgressAndDropsAudio()
    {
        var h = new Harness();
        h.Feed(Audio.Silence(1));
        h.Feed(SpeechLike(1));
        Assert.True(h.Pipeline.InSegment);

        h.Pipeline.Suppressed = true;
        h.Feed(SpeechLike(1)); // arrives while muted: must be thrown away
        Assert.Contains(h.Events, e => e.Kind == SegmentEventKind.Discarded);
        Assert.False(h.Pipeline.InSegment);
        Assert.False(h.Pipeline.Listening);

        h.Pipeline.Suppressed = false;
        h.Feed(Audio.Silence(2));
        Assert.DoesNotContain(h.Events, e => e.Kind == SegmentEventKind.Final); // nothing glued together across the gap
    }

    [Fact]
    public void AudioAfterUnmutingStartsAFreshUtterance()
    {
        var h = new Harness();
        h.Feed(Audio.Silence(1));
        h.Feed(SpeechLike(1));
        h.Pipeline.Suppressed = true;
        h.Feed(Audio.Silence(0.5));
        h.Pipeline.Suppressed = false;
        h.Feed(Audio.Concat(Audio.Silence(0.5), SpeechLike(1.5), Audio.Silence(2)));
        var final = Assert.Single(h.Events, e => e.Kind == SegmentEventKind.Final);
        Assert.InRange(final.DurationMs, 1700, 2300); // only the post-unmute speech, no remnant of the first
    }

    [Fact]
    public void StarvationDiscardsTheUtteranceInProgress()
    {
        // The engine mutes the default mic by simply not delivering samples: no flag flips, audio just stops.
        var h = new Harness();
        h.Feed(Audio.Silence(1));
        h.Feed(SpeechLike(1));
        Assert.True(h.Pipeline.InSegment);

        h.Now += 600; // > 500 ms with nothing written
        h.Pipeline.ProcessAvailable();
        Assert.False(h.Pipeline.InSegment);
        Assert.Contains(h.Events, e => e.Kind == SegmentEventKind.Discarded);

        h.Feed(Audio.Silence(2)); // audio resumes: no Final from the discarded utterance
        Assert.DoesNotContain(h.Events, e => e.Kind == SegmentEventKind.Final);
    }

    [Fact]
    public void StarvationWhileIdleIsHarmless()
    {
        var h = new Harness();
        h.Feed(Audio.Silence(1));
        h.Now += 5000;
        for (int i = 0; i < 5; i++) h.Pipeline.ProcessAvailable();
        Assert.Empty(h.Events);
    }

    [Fact]
    public void TriggeredModeStaysClosedUntilTheGateIsOpened()
    {
        var h = new Harness(SttMode.Triggered);
        Assert.False(h.Pipeline.Gate);
        Assert.False(h.Pipeline.Listening);
        h.Feed(Audio.Concat(Audio.Silence(1), SpeechLike(1.5), Audio.Silence(1)));
        Assert.Empty(h.Events); // speaking with the gate closed records nothing

        h.Pipeline.Gate = true;
        h.Feed(Audio.Concat(SpeechLike(1.5)));
        h.Pipeline.Gate = false;
        h.Feed(Audio.Silence(1));
        var final = Assert.Single(h.Events, e => e.Kind == SegmentEventKind.Final);
        Assert.InRange(final.DurationMs, 1700, 2300);
    }

    [Fact]
    public void ReportsListeningAndSpeakingChanges()
    {
        var h = new Harness();
        h.Feed(Audio.Silence(1));
        Assert.Equal((true, false), h.States[^1]);
        h.Feed(SpeechLike(1));
        Assert.Contains((true, true), h.States);
        h.Pipeline.Suppressed = true;
        h.Pipeline.ProcessAvailable();
        Assert.Equal((false, false), h.States[^1]);
        // Only changes are reported.
        Assert.Equal(h.States.Count, h.States.Zip(h.States.Skip(1), (a, b) => a != b).Count(x => x) + 1);
    }

    [Fact]
    public void FallingBehindDropsTheOldestAudioWithoutFailing()
    {
        // The ring holds 60 s rounded up to a power of two (1,048,576 samples = 65.5 s). Write 80 s without ever reading.
        var h = new Harness();
        for (int i = 0; i < 80; i++) h.Pipeline.Write(Audio.Silence(1));
        int frames = h.Pipeline.ProcessAvailable();

        Assert.Equal(1_048_576 / Segmenter.FrameSamples, frames);                    // only the newest 65.5 s were processed
        Assert.Equal(80 * Audio.Rate - 1_048_576, h.Pipeline.DroppedSamples);        // and every lost sample was counted
    }

    [Fact]
    public void ResetClearsBufferedAudioAndTheUtterance()
    {
        var h = new Harness();
        h.Feed(Audio.Silence(1));
        h.Feed(SpeechLike(1));
        Assert.True(h.Pipeline.InSegment);
        h.Pipeline.Reset();
        Assert.False(h.Pipeline.InSegment);
        Assert.Equal(0, h.Pipeline.ProcessAvailable());
    }

    [Fact]
    public void WorkerThreadProcessesAudioWrittenFromAnotherThread()
    {
        var events = new List<SegmentEvent>();
        using var pipeline = new AudioPipeline(new EnergyVad(), new SegmenterOptions { PublishPartials = false }, e => { lock (events) events.Add(e); });
        pipeline.Start();

        // Real-time-ish producer: 20 ms of audio every ~1 ms of wall clock (20x faster than real time).
        var audio = Audio.Concat(Audio.Silence(1), SpeechLike(1.5), Audio.Silence(2));
        for (int i = 0; i < audio.Length; i += 320)
        {
            pipeline.Write(audio.AsSpan(i, Math.Min(320, audio.Length - i)));
            Thread.Sleep(1);
        }

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline) { lock (events) if (events.Any(e => e.Kind == SegmentEventKind.Final)) break; Thread.Sleep(10); }
        lock (events) Assert.Single(events, e => e.Kind == SegmentEventKind.Final);
    }

    [Fact]
    public void ErrorsInTheSinkAreReportedNotFatal()
    {
        var errors = new List<Exception>();
        int calls = 0;
        using var pipeline = new AudioPipeline(new EnergyVad(), new SegmenterOptions { PublishPartials = false },
            _ => { calls++; throw new InvalidOperationException("sink failed"); }, onError: errors.Add);
        pipeline.Start();
        var audio = Audio.Concat(Audio.Silence(1), SpeechLike(1), Audio.Silence(1.5));
        for (int i = 0; i < audio.Length; i += 320) { pipeline.Write(audio.AsSpan(i, Math.Min(320, audio.Length - i))); Thread.Sleep(1); }
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline && errors.Count == 0) Thread.Sleep(10);
        Assert.NotEmpty(errors);
        Assert.True(calls >= 1);
        // Worker is still alive: it keeps consuming audio.
        pipeline.Write(Audio.Silence(0.5));
        Thread.Sleep(100);
    }
}
