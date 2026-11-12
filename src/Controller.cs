using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FrooxEngine;

namespace SpeechToText;

/// <summary>Everything that forces the audio pipeline to be rebuilt when it changes.</summary>
internal sealed record PipelineConfig(
    SttMode Mode, VadKind Vad, float VadSensitivity, int SilenceMs, int MaxSegmentSec, bool Partials,
    SttModel Model, string Language, bool Translate, int Threads);

/// <summary>
/// Owns the running speech pipeline: starts it when a mode other than Off is chosen, rebuilds it when the relevant
/// settings change, stops it on Off. Driven by a 4 Hz timer; nothing here runs on the engine's threads.
/// <code>
/// mic → AudioTap → AudioPipeline (VAD + Segmenter) → Transcriber (Whisper) → Publisher (User/STT.* variables)
/// </code>
/// </summary>
internal static class SttController
{
    private sealed class Running
    {
        public PipelineConfig Config = null!;
        public IVad Vad = null!;
        public AudioPipeline Pipeline = null!;
        public AudioTap Tap = null!;
        public WhisperEngine Engine = null!;
        public Transcriber Transcriber = null!;
    }

    private static readonly object gate = new();
    private static Running? running;
    private static bool starting;
    private static int version;
    private static Timer? timer;
    private static long lastStatMs;
    private static long lastPartialPublishMs;
    private static long retryAfterMs;           // back-off after a failed start, so a missing model isn't retried 4 times a second
    private static PipelineConfig? failedConfig;

    // Triggered mode (SPEC 6.2): the accepted state of User/STT.Trigger in the world we listen for, set by the Publisher.
    private static volatile bool triggerRequested;
    private static readonly TriggerAutoOff autoOff = new();
    private static bool autoOffReported;

    public static void Start() => timer ??= new Timer(_ => Tick(), null, 1500, 250);

    /// <summary>Called by the Publisher (world thread) whenever the accepted trigger state may have changed.</summary>
    public static void SetTrigger(bool open)
    {
        triggerRequested = open;
        ApplyGate();
    }

    /// <summary>Continuous mode always listens; Triggered mode listens exactly while the trigger is open.</summary>
    private static void ApplyGate()
    {
        Running? r;
        lock (gate) r = running;
        if (r != null) r.Pipeline.Gate = r.Config.Mode == SttMode.Continuous || triggerRequested;
    }

    public static void Poke() => ThreadPool.QueueUserWorkItem(_ => { failedConfig = null; Tick(); });

    private static PipelineConfig ReadConfig() => new(
        SpeechToTextMod.Get(SpeechToTextMod.modeKey),
        SpeechToTextMod.Get(SpeechToTextMod.vadKey),
        SpeechToTextMod.Get(SpeechToTextMod.vadSensKey),
        SpeechToTextMod.Get(SpeechToTextMod.silenceKey),
        SpeechToTextMod.Get(SpeechToTextMod.maxSegKey),
        SpeechToTextMod.Get(SpeechToTextMod.partialsKey),
        SpeechToTextMod.Get(SpeechToTextMod.modelKey),
        (SpeechToTextMod.Get(SpeechToTextMod.languageKey) ?? "auto").Trim().ToLowerInvariant(),
        SpeechToTextMod.Get(SpeechToTextMod.translateKey),
        SpeechToTextMod.Get(SpeechToTextMod.threadsKey));

    /// <summary>English-only models can only do English; "auto"/"" means let Whisper detect the language.</summary>
    private static TranscribeRequest RequestFor(PipelineConfig cfg)
    {
        if (ModelCatalog.IsEnglishOnly(cfg.Model)) return new TranscribeRequest("en", Translate: false);
        return new TranscribeRequest(cfg.Language.Length == 0 ? "auto" : cfg.Language, cfg.Translate);
    }

    private static void Tick()
    {
        try
        {
            PipelineConfig cfg = ReadConfig();
            if (cfg.Mode == SttMode.Off) { Stop(); failedConfig = null; return; }

            Running? r;
            bool isStarting;
            lock (gate) { r = running; isStarting = starting; }

            if (r == null)
            {
                if (!isStarting && (cfg != failedConfig || Environment.TickCount64 > retryAfterMs)) BeginStart(cfg);
                return;
            }
            if (r.Config != cfg)
            {
                SpeechToTextMod.Msg("[pipeline] settings changed, restarting");
                Stop();
                return; // the next tick starts it again with the new settings
            }

            var engine = Engine.Current;
            if (engine?.AudioSystem == null) return;

            // The engine silences a muted default mic by not delivering samples at all, so the audio callback can't tell
            // us: re-evaluate here too. This is what makes "Listening" drop within a quarter second of muting.
            r.Pipeline.Suppressed = Suppression.IsSuppressed(engine, engine.AudioSystem.DefaultAudioInput);

            ApplyGate();
            if (r.Config.Mode == SttMode.Triggered)
            {
                int limit = SpeechToTextMod.Get(SpeechToTextMod.triggerAutoOffKey);
                if (autoOff.ShouldSwitchOff(triggerRequested, limit))
                {
                    if (!autoOffReported)
                    {
                        autoOffReported = true;
                        SpeechToTextMod.Msg($"[trigger] open for {autoOff.OpenFor.TotalSeconds:F0} s, switching it off (auto-off is {limit} s)");
                        Notify.Info($"Switched itself off after {limit} s (auto-off setting).");
                    }
                    Publisher.ForceTriggerOff(); // retried every tick until the value really is false
                }
                else if (!triggerRequested) autoOffReported = false;
            }

            if (SpeechToTextMod.Get(SpeechToTextMod.debugKey)) LogStats(r, engine);
        }
        catch (Exception e) { SpeechToTextMod.Error("[pipeline] tick failed: " + e); }
    }

    private static void BeginStart(PipelineConfig cfg)
    {
        var engine = Engine.Current;
        if (engine?.AudioSystem == null) return; // engine not ready yet; the next tick tries again

        int myVersion;
        lock (gate) { if (starting || running != null) return; starting = true; myVersion = ++version; }

        Task.Run(async () =>
        {
            IVad? vad = null;
            WhisperEngine? whisper = null;
            Transcriber? transcriber = null;
            try
            {
                WhisperEngine.ConfigureRuntime(ModPaths.NativeAnchor, SpeechToTextMod.Get(SpeechToTextMod.backendKey));
                var sw = System.Diagnostics.Stopwatch.StartNew();

                vad = await CreateVadAsync(cfg);
                whisper = await CreateEngineAsync(cfg);
                SpeechToTextMod.Msg($"[pipeline] models ready in {sw.ElapsedMilliseconds} ms (runtime: {WhisperEngine.LoadedRuntime})");

                lock (gate)
                {
                    // Mode switched off or settings changed while the models were loading: this start is stale.
                    if (myVersion != version) return;

                    var request = RequestFor(cfg);
                    transcriber = new Transcriber(whisper, () => request,
                        () => new TextFilter(SpeechToTextMod.Get(SpeechToTextMod.maxTextKey), SpeechToTextMod.Get(SpeechToTextMod.profanityKey)),
                        OnTranscription,
                        warn: m => SpeechToTextMod.Warn("[stt] " + m),
                        onError: e => SpeechToTextMod.Error("[stt] recognition failed: " + e.Message));

                    var options = new SegmenterOptions
                    {
                        Mode = cfg.Mode,
                        SilenceMs = cfg.SilenceMs,
                        MaxSegmentSec = cfg.MaxSegmentSec,
                        PublishPartials = cfg.Partials,
                    };
                    var t = transcriber;
                    var pipeline = new AudioPipeline(vad, options, e => { LogSegment(e); t.Submit(e); }, OnState,
                        e => SpeechToTextMod.Error("[pipeline] worker error: " + e));
                    var tap = new AudioTap(engine, pipeline);
                    pipeline.Start();
                    tap.Start();
                    running = new Running { Config = cfg, Vad = vad, Pipeline = pipeline, Tap = tap, Engine = whisper, Transcriber = transcriber };
                    ApplyGate(); // Triggered mode starts closed unless the trigger is already on
                    lastStatMs = Environment.TickCount64;
                    failedConfig = null;
                    vad = null; whisper = null; transcriber = null; // owned by Running now
                    SpeechToTextMod.Msg($"[pipeline] started: {cfg.Mode}, {cfg.Vad} detector, model {cfg.Model}, language {RequestFor(cfg).Language}");
                    AnnounceStarted(cfg);
                }
            }
            catch (Exception e)
            {
                SpeechToTextMod.Error("[pipeline] start failed (will retry in 30 s or when settings change): " + e.Message);
                Notify.Error("Could not start: " + e.Message + " (retrying in 30 s; see the log)");
                failedConfig = cfg;
                retryAfterMs = Environment.TickCount64 + 30_000;
            }
            finally
            {
                transcriber?.Dispose();
                whisper?.Dispose();
                (vad as IDisposable)?.Dispose();
                lock (gate) starting = false;
            }
        });
    }

    private static async Task<IVad> CreateVadAsync(PipelineConfig cfg)
    {
        if (cfg.Vad == VadKind.Energy) return new EnergyVad(cfg.VadSensitivity);
        try
        {
            using var models = new ModelManager(ModPaths.Models);
            if (!models.IsInstalled(ModelCatalog.SileroVad)) Notify.Info("Downloading the voice detector once (1 MB)...");
            string path = await models.EnsureAsync(ModelCatalog.SileroVad, ProgressLogger("Silero VAD"));
            return new SileroVad(path);
        }
        catch (Exception e)
        {
            SpeechToTextMod.Error($"[pipeline] Silero detector unavailable, using the energy detector instead: {e.Message}");
            Notify.Warn("The voice detector could not be loaded, so a simpler fallback is used.");
            return new EnergyVad(cfg.VadSensitivity);
        }
    }

    private static async Task<WhisperEngine> CreateEngineAsync(PipelineConfig cfg)
    {
        using var models = new ModelManager(ModPaths.Models);
        var file = ModelCatalog.Get(cfg.Model);
        bool download = !models.IsInstalled(file);
        if (download) Notify.Info($"Downloading the speech model once ({ModelCatalog.ApproxMegabytes(cfg.Model)} MB). Listening starts when it is ready.");
        string path = await models.EnsureAsync(file, ProgressLogger($"Whisper {cfg.Model}"));
        if (download) Notify.Info("Speech model downloaded.");
        bool gpu = SpeechToTextMod.Get(SpeechToTextMod.backendKey) != SttBackend.Cpu;
        var engine = new WhisperEngine(path, cfg.Threads, gpu);
        try
        {
            // The first run on each audio-context size builds a processor and is several times slower; pay for the sizes that
            // short utterances use now, not in the middle of someone's first sentence.
            var request = RequestFor(cfg);
            foreach (int seconds in new[] { 3, 7 }) await engine.TranscribeAsync(new float[Segmenter.SampleRate * seconds], request);
            return engine;
        }
        catch { engine.Dispose(); throw; }
    }

    /// <summary>The one-time privacy notice (SPEC G5) and a short "what now" line each time the pipeline comes up.</summary>
    private static void AnnounceStarted(PipelineConfig cfg)
    {
        if (!SpeechToTextMod.Get(SpeechToTextMod.privacyShownKey))
        {
            SpeechToTextMod.Set(SpeechToTextMod.privacyShownKey, true);
            Notify.Info("<b>Speech to text is on.</b> What you say is transcribed on this PC and published as text variables " +
                        "(User/STT.*) that everyone in the session can read. It pauses while you are muted or whispering. " +
                        "Turn it off with Mode: Off in the mod settings.");
        }
        Notify.Info(cfg.Mode == SttMode.Continuous
            ? "Listening continuously."
            : "Ready. Set User/STT.Trigger to true to start listening, false to stop.");
    }

    private static IProgress<(long Done, long Total)> ProgressLogger(string what)
    {
        int last = -1;
        return new Progress<(long Done, long Total)>(p =>
        {
            if (p.Total <= 0) return;
            int pct = (int)(p.Done * 100 / p.Total) / 10 * 10; // log every 10 %
            if (pct == last) return;
            last = pct;
            SpeechToTextMod.Msg($"[model] downloading {what}: {pct}%");
        });
    }

    public static void Stop()
    {
        Running? r;
        lock (gate) { version++; r = running; running = null; }
        if (r == null) return;
        try
        {
            r.Tap.Dispose();         // detach from the mic first (waits for an in-flight callback)
            r.Pipeline.Dispose();    // then the audio worker
            r.Transcriber.Dispose(); // then recognition (abandons queued work)
            r.Engine.Dispose();
            (r.Vad as IDisposable)?.Dispose();
        }
        catch (Exception e) { SpeechToTextMod.Error("[pipeline] stop failed: " + e); }
        Publisher.Update(s => s with { Listening = false, Speaking = false, Partial = "" });
        SpeechToTextMod.Msg("[pipeline] stopped");
    }

    // ---- callbacks ------------------------------------------------------------------------------------------------

    private static void OnState(bool listening, bool speaking)
    {
        Publisher.Update(s => s with { Listening = listening, Speaking = speaking });
        if (SpeechToTextMod.Get(SpeechToTextMod.debugKey)) SpeechToTextMod.Msg($"[state] listening={listening} speaking={speaking}");
    }

    private static void LogSegment(SegmentEvent e)
    {
        if (!SpeechToTextMod.Get(SpeechToTextMod.debugKey)) return;
        string audio = (e.DurationMs / 1000.0).ToString("F2");
        string voiced = (e.VoicedMs / 1000.0).ToString("F2");
        switch (e.Kind)
        {
            case SegmentEventKind.Final: SpeechToTextMod.Msg($"[segment] FINAL #{e.UtteranceId}: {audio} s of audio, {voiced} s voiced"); break;
            case SegmentEventKind.Partial: SpeechToTextMod.Msg($"[segment] partial #{e.UtteranceId}: {audio} s so far, {voiced} s voiced"); break;
            default: SpeechToTextMod.Msg($"[segment] discarded #{e.UtteranceId} ({voiced} s voiced)"); break;
        }
    }

    /// <summary>Recognition results from the Transcriber thread: this is where text reaches the variables.</summary>
    private static void OnTranscription(TranscriptionEvent e)
    {
        bool debug = SpeechToTextMod.Get(SpeechToTextMod.debugKey);
        switch (e.Kind)
        {
            case TranscriptionKind.Partial:
            {
                // PartialRateHz: never publish partials faster than configured.
                long now = Environment.TickCount64;
                double minGap = 1000.0 / Math.Max(1.0, SpeechToTextMod.Get(SpeechToTextMod.partialRateKey));
                if (now - Interlocked.Read(ref lastPartialPublishMs) < minGap) break;
                Interlocked.Exchange(ref lastPartialPublishMs, now);
                Publisher.Update(s => s with { Partial = e.Text });
                if (debug) SpeechToTextMod.Msg($"[stt] partial #{e.UtteranceId} ({e.AudioMs / 1000.0:F1} s, {e.Inference.TotalMilliseconds:F0} ms): \"{e.Text}\"");
                break;
            }
            case TranscriptionKind.Final:
                if (e.Text.Length > 0)
                {
                    Publisher.Update(s => s with { Text = e.Text, Partial = "", Sequence = s.Sequence + 1, Language = e.Language });
                    Publisher.Poke(); // write now instead of waiting for the next 4 Hz tick
                }
                else Publisher.Update(s => s with { Partial = "" });
                if (debug)
                    SpeechToTextMod.Msg($"[stt] FINAL #{e.UtteranceId} ({e.AudioMs / 1000.0:F1} s audio): inference {e.Inference.TotalMilliseconds:F0} ms, " +
                                        $"{e.SinceSubmitted.TotalMilliseconds:F0} ms from end of utterance to published: \"{e.Text}\"");
                break;
            case TranscriptionKind.Cleared:
                Publisher.Update(s => s with { Partial = "" });
                break;
        }
    }

    private static void LogStats(Running r, Engine engine)
    {
        long now = Environment.TickCount64;
        if (now - lastStatMs < 5000) return;
        double seconds = (now - lastStatMs) / 1000.0;
        lastStatMs = now;
        var s = r.Tap.TakeStats();
        string? reason = Suppression.Reason(engine, engine.AudioSystem.DefaultAudioInput);
        SpeechToTextMod.Msg(
            $"[audio] '{r.Tap.InputName}' {s.Rate} Hz: {s.Samples / seconds:F0} samples/s ({s.Callbacks / seconds:F0} callbacks/s), " +
            $"rms {s.RmsDb:F1} dB, peak {s.PeakDb:F1} dB | suppressed: {reason ?? "no"} | listening={r.Pipeline.Listening} speaking={r.Pipeline.Speaking} " +
            $"| frames={r.Pipeline.FramesProcessed} dropped={r.Pipeline.DroppedSamples} | queued={r.Transcriber.QueuedFinals} partials dropped={r.Transcriber.DroppedPartials}");
    }

    // ---- test hook ------------------------------------------------------------------------------------------------

    /// <summary>
    /// McpLink test hook: simulate someone talking non-stop for <paramref name="seconds"/> by submitting a recorded sentence as a
    /// finished utterance every <paramref name="everyMs"/> ms, so frame-time cost can be measured without a human talking.
    /// Needs the pipeline to be running (Mode on). Returns a one-line summary.
    /// </summary>
    public static string DebugStress(int seconds, int everyMs)
    {
        Running? r;
        lock (gate) r = running;
        if (r == null) return "pipeline not running (set a mode first)";
        if (!File.Exists(ModPaths.TestAudio)) return "test audio missing: " + ModPaths.TestAudio;

        byte[] bytes = File.ReadAllBytes(ModPaths.TestAudio);
        var samples = new float[(bytes.Length - 44) / 2];
        for (int i = 0; i < samples.Length; i++) samples[i] = BitConverter.ToInt16(bytes, 44 + i * 2) / 32768f;

        var transcriber = r.Transcriber;
        Task.Run(async () =>
        {
            var end = Environment.TickCount64 + seconds * 1000L;
            long id = 1_000_000;
            int n = 0;
            while (Environment.TickCount64 < end)
            {
                transcriber.Submit(new SegmentEvent { Kind = SegmentEventKind.Final, UtteranceId = ++id, Samples = samples, VoicedMs = 3000 });
                n++;
                await Task.Delay(everyMs);
            }
            SpeechToTextMod.Msg($"[stress] submitted {n} utterances of {samples.Length / 16000.0:F1} s over {seconds} s");
        });
        return $"stress started: a {samples.Length / 16000.0:F1} s sentence every {everyMs} ms for {seconds} s";
    }
}
