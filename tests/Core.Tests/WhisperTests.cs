using System.Diagnostics;

namespace SpeechToText.Tests;

/// <summary>Shared, expensive objects: the Whisper model is loaded once for the whole class.</summary>
public sealed class WhisperFixture : IDisposable
{
    public WhisperEngine? Engine { get; }
    public string? SileroPath { get; }

    public WhisperFixture()
    {
        if (File.Exists(Repo.Model("ggml-base.en.bin"))) Engine = new WhisperEngine(Repo.Model("ggml-base.en.bin"), threads: 4);
        if (File.Exists(Repo.Model("ggml-silero-v5.1.2.bin"))) SileroPath = Repo.Model("ggml-silero-v5.1.2.bin");
    }

    public void Dispose() => Engine?.Dispose();
}

/// <summary>
/// Integration tests with the real models (skipped when models\ is empty). They exercise the whole core: audio →
/// VAD → segmenter → Whisper → text filter, on speech recorded with the Windows TTS voice.
/// </summary>
public class WhisperTests : IClassFixture<WhisperFixture>
{
    private readonly WhisperFixture _fx;
    public WhisperTests(WhisperFixture fx) => _fx = fx;

    private const string Base = "ggml-base.en.bin";
    private const string Silero = "ggml-silero-v5.1.2.bin";

    private static readonly string[] MonologuePhrases =
    {
        "quick brown fox",
        "build together in virtual reality",
        "longer sentence",
    };

    /// <summary>Runs audio through VAD + segmenter and returns the Final segments (partials off).</summary>
    private static List<SegmentEvent> Segment(IVad vad, float[] audio, SttMode mode = SttMode.Continuous, bool gate = true)
    {
        var events = new List<SegmentEvent>();
        var seg = new Segmenter(new SegmenterOptions { Mode = mode, PublishPartials = false }, vad, events.Add);
        Audio.Feed(seg, audio, gate);
        Audio.Feed(seg, Audio.Silence(2), gate: mode == SttMode.Triggered ? false : gate); // flush: lets the last utterance end
        return events.Where(e => e.Kind == SegmentEventKind.Final).ToList();
    }

    private async Task<List<string>> Transcribe(IEnumerable<SegmentEvent> finals)
    {
        var filter = new TextFilter();
        var texts = new List<string>();
        foreach (var f in finals)
        {
            var t = await _fx.Engine!.TranscribeAsync(f.Samples, new TranscribeRequest("en"));
            texts.Add(filter.Apply(t.Text, f.VoicedMs));
        }
        return texts;
    }

    // ---- Whisper engine --------------------------------------------------------------------------------------------

    [RequiresModelFact(Base)]
    public async Task Engine_TranscribesASentence()
    {
        var r = await _fx.Engine!.TranscribeAsync(Audio.ReadWav(Repo.Fixture("hello.wav")), new TranscribeRequest("en"));
        Assert.Contains("test of local speech to text", Audio.Norm(r.Text));
        Assert.Equal("en", r.Language);
        Assert.True(r.Elapsed < TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void AudioContext_LadderCoversTheClipWithMargin()
    {
        Assert.Equal(250, WhisperEngine.AudioContextFor(16000 * 4));   // 4 s + 0.5 s = 225 <= 250
        Assert.Equal(500, WhisperEngine.AudioContextFor(16000 * 5));   // 5 s + 0.5 s = 275 > 250
        Assert.Equal(1000, WhisperEngine.AudioContextFor(16000 * 15));
        Assert.Equal(1500, WhisperEngine.AudioContextFor(16000 * 25)); // 1275 needs the full window
        Assert.Equal(1500, WhisperEngine.AudioContextFor(16000 * 40));
        Assert.Equal(250, WhisperEngine.AudioContextFor(100));          // tiny clips still get a usable context
    }

    [RequiresModelFact(Base, Silero)]
    public async Task Engine_AdaptiveContext_IsMuchFasterAndGivesTheSameText()
    {
        using var vad = new SileroVad(_fx.SileroPath!);
        var pieces = Segment(vad, Audio.Concat(Audio.Silence(1), Audio.ReadWav(Repo.Fixture("monologue.wav")), Audio.Silence(1)))
            .Select(f => f.Samples).ToList();
        pieces.Add(Audio.ReadWav(Repo.Fixture("hello.wav")));
        pieces.Add(Audio.ReadWav(Repo.Fixture("short.wav")));

        using var fixedEngine = new WhisperEngine(Repo.Model(Base), 4, adaptiveContext: false);
        using var adaptive = new WhisperEngine(Repo.Model(Base), 4, adaptiveContext: true);
        var req = new TranscribeRequest("en");
        foreach (var e in new[] { fixedEngine, adaptive })
            foreach (var p in pieces) await e.TranscribeAsync(p, req); // warm up every processor/context size

        TimeSpan tFixed = TimeSpan.Zero, tAdaptive = TimeSpan.Zero;
        foreach (var p in pieces)
        {
            var a = await fixedEngine.TranscribeAsync(p, req);
            var b = await adaptive.TranscribeAsync(p, req);
            tFixed += a.Elapsed; tAdaptive += b.Elapsed;
            Console.WriteLine($"[timing] {p.Length / 16000.0:F1} s clip: fixed {a.Elapsed.TotalMilliseconds:F0} ms, adaptive {b.Elapsed.TotalMilliseconds:F0} ms (ctx {WhisperEngine.AudioContextFor(p.Length)}): \"{b.Text.Trim()}\"");
            Assert.Equal(Audio.Norm(a.Text), Audio.Norm(b.Text)); // shrinking the window must not change what is recognised
        }
        Console.WriteLine($"[timing] total fixed {tFixed.TotalMilliseconds:F0} ms vs adaptive {tAdaptive.TotalMilliseconds:F0} ms");
        Assert.True(tAdaptive < tFixed * 0.7, $"adaptive {tAdaptive.TotalMilliseconds:F0} ms vs fixed {tFixed.TotalMilliseconds:F0} ms");
    }

    [Fact]
    public void Engine_TokenCapAndDeadlineGrowWithTheClip()
    {
        Assert.Equal(70, WhisperEngine.MaxTokensFor(250));
        Assert.Equal(120, WhisperEngine.MaxTokensFor(500));
        Assert.Equal(220, WhisperEngine.MaxTokensFor(1000));
        Assert.Equal(224, WhisperEngine.MaxTokensFor(1500));
        Assert.Equal(TimeSpan.FromSeconds(2), WhisperEngine.DeadlineFor(16000));      // never under 2 s
        Assert.Equal(TimeSpan.FromSeconds(4), WhisperEngine.DeadlineFor(16000 * 6));  // 1 s + half the clip
        Assert.Equal(TimeSpan.FromSeconds(16), WhisperEngine.DeadlineFor(16000 * 30));
    }

    [RequiresModelFact(Base)]
    public async Task Engine_RunawayDecodesAreBoundedAndTheirOutputIsTamed()
    {
        // Humming, singing and sustained tones used to make Whisper loop for 10-13 s and freeze the game's frame rate.
        // Each of these now has to finish quickly, and what's left after the text filter must be short.
        var clips = new (string Name, float[] Audio)[]
        {
            ("hum", Audio.ReadWav(Repo.Fixture("hum.wav"))),
            ("oh", Audio.ReadWav(Repo.Fixture("oh.wav"))),
            ("la", Audio.ReadWav(Repo.Fixture("la.wav"))),
            ("stutter", Audio.ReadWav(Repo.Fixture("stutter.wav"))),
            ("tone", Audio.Sine(220, 3, 0.3)),
        };
        var filter = new TextFilter();
        foreach (var (name, audio) in clips)
        {
            var r = await _fx.Engine!.TranscribeAsync(audio, new TranscribeRequest("en"));
            string cleaned = filter.Apply(r.Text, voicedMs: 3000);
            Console.WriteLine($"[timing] bounded '{name}' ({audio.Length / 16000.0:F1} s): {r.Elapsed.TotalMilliseconds:F0} ms, timedOut={r.TimedOut} -> \"{cleaned}\"");
            Assert.True(r.Elapsed < TimeSpan.FromSeconds(4), $"'{name}' took {r.Elapsed.TotalSeconds:F1} s");
            Assert.True(cleaned.Length <= 40, $"'{name}' produced {cleaned.Length} characters after filtering: {cleaned}");
        }
    }

    [RequiresModelFact(Base)]
    public async Task Engine_AwkwardAudio_TimingSurvey()
    {
        // Looking for inputs that make Whisper's decoder run away (what froze the game in the first live test).
        var clips = new Dictionary<string, float[]>
        {
            ["stutter"] = Audio.ReadWav(Repo.Fixture("stutter.wav")),
            ["hum"] = Audio.ReadWav(Repo.Fixture("hum.wav")),
            ["la"] = Audio.ReadWav(Repo.Fixture("la.wav")),
            ["oh"] = Audio.ReadWav(Repo.Fixture("oh.wav")),
            ["white noise -20 dB 3 s"] = Audio.Noise(3, -20, 3),
            ["white noise -30 dB 6 s"] = Audio.Noise(6, -30, 4),
            ["tone 220 Hz 3 s"] = Audio.Sine(220, 3, 0.3),
            ["tone sweep-ish 2 s"] = Audio.Concat(Audio.Sine(300, 0.5, 0.3), Audio.Sine(600, 0.5, 0.3), Audio.Sine(900, 0.5, 0.3), Audio.Sine(300, 0.5, 0.3)),
        };
        foreach (var (name, audio) in clips)
        {
            var r = await _fx.Engine!.TranscribeAsync(audio, new TranscribeRequest("en"));
            Console.WriteLine($"[timing] awkward '{name}' ({audio.Length / 16000.0:F1} s): {r.Elapsed.TotalMilliseconds:F0} ms -> \"{r.Text}\"");
        }
    }

    [RequiresModelFact(Base)]
    public async Task Engine_CancellationReport_HowFastDoesARunawayCallStop()
    {
        var hum = Audio.ReadWav(Repo.Fixture("hum.wav"));
        using var cts = new CancellationTokenSource();
        var sw = Stopwatch.StartNew();
        cts.CancelAfter(300);
        string outcome;
        try { var r = await _fx.Engine!.TranscribeAsync(hum, new TranscribeRequest("en"), cts.Token); outcome = $"completed anyway ({r.Elapsed.TotalMilliseconds:F0} ms)"; }
        catch (OperationCanceledException) { outcome = "cancelled"; }
        Console.WriteLine($"[timing] cancel after 300 ms on a runaway clip: {outcome}, returned after {sw.ElapsedMilliseconds} ms");
        // The engine must still work afterwards.
        var ok = await _fx.Engine!.TranscribeAsync(Audio.ReadWav(Repo.Fixture("short.wav")), new TranscribeRequest("en"));
        Assert.Contains("hello", Audio.Norm(ok.Text));
    }

    [RequiresModelFact(Base)]
    public async Task Engine_AutoLanguageDetectsEnglish()
    {
        var r = await _fx.Engine!.TranscribeAsync(Audio.ReadWav(Repo.Fixture("hello.wav")), new TranscribeRequest("auto"));
        Assert.Contains("speech", Audio.Norm(r.Text));
    }

    [RequiresModelFact(Base)]
    public async Task Engine_ShortClipsUnderOneSecondAreHandled()
    {
        var full = Audio.ReadWav(Repo.Fixture("short.wav"));
        var clipped = full[..(Audio.Rate * 6 / 10)]; // 600 ms: whisper.cpp alone would refuse this
        var r = await _fx.Engine!.TranscribeAsync(clipped, new TranscribeRequest("en"));
        Assert.NotNull(r.Text); // must not throw; content of a clipped word is unspecified
        var whole = await _fx.Engine!.TranscribeAsync(full, new TranscribeRequest("en"));
        Assert.Contains("hello", Audio.Norm(whole.Text));
    }

    [RequiresModelFact(Base)]
    public async Task Engine_SilenceAndFaintNoiseProduceNothingAfterFiltering()
    {
        var filter = new TextFilter();
        foreach (var audio in new[] { Audio.Silence(3), Audio.Noise(3, -60) })
        {
            var r = await _fx.Engine!.TranscribeAsync(audio, new TranscribeRequest("en"));
            Assert.Equal("", filter.Apply(r.Text, voicedMs: 0));
        }
    }

    [RequiresModelFact(Base)]
    public async Task Engine_SerializesConcurrentRequestsAndHonoursCancellation()
    {
        var audio = Audio.ReadWav(Repo.Fixture("hello.wav"));
        var first = _fx.Engine!.TranscribeAsync(audio, new TranscribeRequest("en"));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        // Queued behind the first request with an already-cancelled token: must not run.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _fx.Engine!.TranscribeAsync(audio, new TranscribeRequest("en"), cts.Token));
        Assert.Contains("speech", Audio.Norm((await first).Text));

        // Two real concurrent requests both complete correctly.
        var results = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => _fx.Engine!.TranscribeAsync(audio, new TranscribeRequest("en"))));
        Assert.All(results, r => Assert.Contains("speech", Audio.Norm(r.Text)));
    }

    [RequiresModelFact(Base)]
    public async Task Engine_DisposedEngineRefusesWork()
    {
        using var engine = new WhisperEngine(Repo.Model(Base), 2);
        engine.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => engine.TranscribeAsync(new float[16000], new TranscribeRequest("en")));
    }

    // ---- Silero VAD ------------------------------------------------------------------------------------------------

    private static bool[] Flags(IVad vad, float[] audio)
    {
        var flags = new List<bool>();
        for (int i = 0; i + Segmenter.FrameSamples <= audio.Length; i += Segmenter.FrameSamples)
        {
            vad.Push(audio.AsSpan(i, Segmenter.FrameSamples));
            flags.Add(vad.IsSpeech);
        }
        return flags.ToArray();
    }

    [RequiresModelFact(Silero)]
    public void Silero_SilenceAndHissAreNotSpeech()
    {
        using var vad = new SileroVad(_fx.SileroPath!);
        Assert.DoesNotContain(true, Flags(vad, Audio.Silence(5)));
        vad.Reset();
        Assert.DoesNotContain(true, Flags(vad, Audio.Noise(5, -45)));
    }

    [RequiresModelFact(Silero)]
    public void Silero_FindsSpeechAndReleasesSoonAfterItEnds()
    {
        var speech = Audio.ReadWav(Repo.Fixture("hello.wav"));
        using var vad = new SileroVad(_fx.SileroPath!);
        var flags = Flags(vad, Audio.Concat(Audio.Silence(1), speech, Audio.Silence(2)));
        int speechStart = 50, speechEnd = 50 + speech.Length / Segmenter.FrameSamples;

        int inside = flags[speechStart..speechEnd].Count(f => f);
        Assert.True(inside > (speechEnd - speechStart) * 0.6, $"only {inside}/{speechEnd - speechStart} speech frames detected");
        Assert.DoesNotContain(true, flags[..(speechStart - 5)]);              // nothing before the speech
        Assert.DoesNotContain(true, flags[(speechEnd + 40)..]);               // released within ~0.8 s of the end
    }

    [RequiresModelFact(Silero)]
    public void Silero_CostReport_CpuPerSecondOfAudio()
    {
        // The detector runs continuously while the mod is on, so its CPU cost matters. Reported, and bounded generously.
        var audio = Audio.Concat(Audio.ReadWav(Repo.Fixture("monologue.wav")), Audio.Silence(3));
        using var vad = new SileroVad(_fx.SileroPath!);
        Flags(vad, audio.AsSpan(0, Audio.Rate * 2).ToArray()); // warm-up
        vad.Reset();
        var sw = Stopwatch.StartNew();
        Flags(vad, audio);
        double seconds = audio.Length / (double)Audio.Rate;
        double cpuPerSecond = sw.Elapsed.TotalSeconds / seconds;
        Console.WriteLine($"[timing] Silero VAD: {sw.ElapsedMilliseconds} ms for {seconds:F1} s of audio = {cpuPerSecond * 100:F1}% of one core in real time");
        Assert.True(cpuPerSecond < 0.5, $"Silero costs {cpuPerSecond * 100:F0}% of a core");
    }

    [RequiresModelFact(Silero)]
    public void Silero_ResetForgetsEarlierAudio()
    {
        var speech = Audio.ReadWav(Repo.Fixture("hello.wav"));
        using var vad = new SileroVad(_fx.SileroPath!);
        Flags(vad, speech);
        vad.Reset();
        Assert.False(vad.IsSpeech);
        Assert.DoesNotContain(true, Flags(vad, Audio.Silence(2)));
    }

    // ---- Whole pipeline --------------------------------------------------------------------------------------------

    private async Task AssertMonologue(IVad vad, float[] audio)
    {
        var finals = Segment(vad, audio);
        Assert.Equal(3, finals.Count);
        Assert.All(finals, f => Assert.True(f.DurationMs >= 800, $"segment of only {f.DurationMs} ms"));
        var texts = await Transcribe(finals);
        for (int i = 0; i < 3; i++)
            Assert.Contains(MonologuePhrases[i], Audio.Norm(texts[i]));
    }

    [RequiresModelFact(Base, Silero)]
    public async Task Pipeline_Monologue_Silero_ThreeUtterancesTranscribedInOrder()
    {
        using var vad = new SileroVad(_fx.SileroPath!);
        await AssertMonologue(vad, Audio.Concat(Audio.Silence(1), Audio.ReadWav(Repo.Fixture("monologue.wav")), Audio.Silence(1)));
    }

    [RequiresModelFact(Base)]
    public async Task Pipeline_Monologue_Energy_ThreeUtterancesTranscribedInOrder() =>
        await AssertMonologue(new EnergyVad(), Audio.Concat(Audio.Silence(1), Audio.ReadWav(Repo.Fixture("monologue.wav")), Audio.Silence(1)));

    [RequiresModelFact(Base, Silero)]
    public async Task Pipeline_NoisyMonologue_Silero()
    {
        using var vad = new SileroVad(_fx.SileroPath!);
        var noisy = Audio.AddNoise(Audio.ReadWav(Repo.Fixture("monologue.wav")), snrDb: 10);
        await AssertMonologue(vad, Audio.Concat(Audio.Noise(1, Audio.RmsDb(noisy) - 10, 5), noisy, Audio.Noise(1, Audio.RmsDb(noisy) - 10, 6)));
    }

    [RequiresModelFact(Base)]
    public async Task Pipeline_NoisyMonologue_Energy()
    {
        var noisy = Audio.AddNoise(Audio.ReadWav(Repo.Fixture("monologue.wav")), snrDb: 10);
        await AssertMonologue(new EnergyVad(), Audio.Concat(Audio.Noise(2, Audio.RmsDb(noisy) - 10, 5), noisy, Audio.Noise(1, Audio.RmsDb(noisy) - 10, 6)));
    }

    [RequiresModelFact(Base, Silero)]
    public void Pipeline_PureSilenceAndNoiseProduceNoSegmentsAtAll()
    {
        using var vad = new SileroVad(_fx.SileroPath!);
        Assert.Empty(Segment(vad, Audio.Silence(20)));
        vad.Reset();
        Assert.Empty(Segment(vad, Audio.Noise(20, -45)));
        Assert.Empty(Segment(new EnergyVad(), Audio.Silence(20)));
        Assert.Empty(Segment(new EnergyVad(), Audio.Noise(20, -55)));
    }

    [RequiresModelFact(Base, Silero)]
    public async Task Pipeline_TriggeredMode_RecordsWhileHeldAndTranscribesOnRelease()
    {
        var speech = Audio.ReadWav(Repo.Fixture("hello.wav"));
        using var vad = new SileroVad(_fx.SileroPath!);
        var events = new List<SegmentEvent>();
        var seg = new Segmenter(new SegmenterOptions { Mode = SttMode.Triggered, PublishPartials = false }, vad, events.Add);

        Audio.Feed(seg, Audio.Silence(1.5), gate: false);
        Audio.Feed(seg, Audio.Concat(speech, Audio.Silence(0.4)), gate: true);   // held through the sentence
        Audio.Feed(seg, Audio.Silence(1.5), gate: false);                        // released

        var final = Assert.Single(events, e => e.Kind == SegmentEventKind.Final);
        Assert.InRange(final.DurationMs, speech.Length * 1000 / Audio.Rate, (speech.Length * 1000 / Audio.Rate) + 400 + 600);
        var texts = await Transcribe(new[] { final });
        Assert.Contains("test of local speech to text", Audio.Norm(texts[0]));
    }

    [RequiresModelFact(Base, Silero)]
    public async Task Pipeline_ResamplingFrom48kHzDoesNotHurtRecognition()
    {
        // Upsample the recording to 48 kHz, then run it through our own 48k -> 16k resampler before recognition.
        var speech16 = Audio.ReadWav(Repo.Fixture("hello.wav"));
        var up = new Resampler(16000, 48000);
        var speech48 = new float[up.MaxOutput(speech16.Length)];
        int n48 = up.Process(speech16, speech48);

        var down = new Resampler(48000, 16000);
        var back = new float[down.MaxOutput(n48)];
        int n16 = down.Process(speech48.AsSpan(0, n48), back);

        var r = await _fx.Engine!.TranscribeAsync(back[..n16], new TranscribeRequest("en"));
        Assert.Contains("test of local speech to text", Audio.Norm(r.Text));
    }

    [RequiresModelFact(Base, Silero)]
    public void FullStack_AudioToText_ThroughPipelineAndTranscriber()
    {
        // Audio in at the front, finished text out at the back: AudioPipeline (VAD + segmenter) -> Transcriber -> Whisper.
        using var vad = new SileroVad(_fx.SileroPath!);
        var events = new List<TranscriptionEvent>();
        using var transcriber = new Transcriber(_fx.Engine!, () => new TranscribeRequest("en"), () => new TextFilter(),
            e => { lock (events) events.Add(e); });
        using var pipeline = new AudioPipeline(vad, new SegmenterOptions { PublishPartials = true }, transcriber.Submit);

        var audio = Audio.Concat(Audio.Silence(1), Audio.ReadWav(Repo.Fixture("monologue.wav")), Audio.Silence(2));
        for (int i = 0; i < audio.Length; i += 960) // 60 ms writes, processed as they arrive (faster than real time)
        {
            pipeline.Write(audio.AsSpan(i, Math.Min(960, audio.Length - i)));
            pipeline.ProcessAvailable();
        }

        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < deadline) { lock (events) if (events.Count(e => e.Kind == TranscriptionKind.Final) >= 3) break; Thread.Sleep(20); }

        TranscriptionEvent[] all; lock (events) all = events.ToArray();
        var finals = all.Where(e => e.Kind == TranscriptionKind.Final).ToList();
        Assert.Equal(3, finals.Count);
        for (int i = 0; i < 3; i++) Assert.Contains(MonologuePhrases[i], Audio.Norm(finals[i].Text));
        Assert.All(finals, f => Assert.Equal("en", f.Language));

        // Partials, if any got through, belong to a still-running utterance and never come after that utterance's final.
        foreach (var f in finals)
        {
            int finalIndex = Array.IndexOf(all, f);
            Assert.DoesNotContain(all.Skip(finalIndex + 1), e => e.Kind == TranscriptionKind.Partial && e.UtteranceId == f.UtteranceId);
        }

        // Recognition time per utterance must leave room in the 1.5 s budget (0.7 s of silence is waited for before this starts).
        // SinceSubmitted would include queueing, which this test inflates by feeding audio far faster than real time.
        Assert.All(finals, f => Assert.True(f.Inference < TimeSpan.FromSeconds(1.0), $"inference took {f.Inference.TotalSeconds:F2} s"));
        Console.WriteLine("[timing] per-utterance recognition: " + string.Join(", ", finals.Select(f => $"{f.AudioMs / 1000.0:F1} s audio -> {f.Inference.TotalSeconds:F2} s")));
    }

    [RequiresModelFact(Base, Silero)]
    public async Task Pipeline_TimingReport_RealTimeFactor()
    {
        // Not a pass/fail speed test (machines differ), but it must stay well faster than real time to be usable.
        var audio = Audio.ReadWav(Repo.Fixture("monologue.wav"));
        await _fx.Engine!.TranscribeAsync(audio[..(Audio.Rate * 2)], new TranscribeRequest("en")); // warm-up
        var sw = Stopwatch.StartNew();
        var r = await _fx.Engine!.TranscribeAsync(audio, new TranscribeRequest("en"));
        double rtf = sw.Elapsed.TotalSeconds / (audio.Length / (double)Audio.Rate);
        Console.WriteLine($"[timing] {audio.Length / (double)Audio.Rate:F1} s of audio in {sw.ElapsedMilliseconds} ms (RTF {rtf:F2})");
        Assert.True(rtf < 1.0, $"real-time factor {rtf:F2}");
        Assert.False(string.IsNullOrWhiteSpace(r.Text));
    }
}
