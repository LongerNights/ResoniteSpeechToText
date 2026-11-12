using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Whisper.net;
using Whisper.net.LibraryLoader;

namespace SpeechToText;

/// <param name="Language">ISO code, or "auto".</param>
/// <param name="Translate">Whisper's translate-to-English task.</param>
public readonly record struct TranscribeRequest(string Language = "auto", bool Translate = false);

/// <param name="TimedOut">The call hit its deadline and was cancelled; <c>Text</c> is empty.</param>
public sealed record Transcription(string Text, string Language, TimeSpan Elapsed, bool TimedOut = false);

public interface ISpeechEngine : IDisposable
{
    /// <summary>Transcribe 16 kHz mono float audio. Calls are serialized; cancel to skip a queued request.</summary>
    Task<Transcription> TranscribeAsync(float[] samples, TranscribeRequest request, CancellationToken ct = default);
}

/// <summary>Local Whisper (whisper.cpp through Whisper.net). One loaded model, one inference at a time.</summary>
public sealed class WhisperEngine : ISpeechEngine
{
    /// <summary>whisper.cpp refuses clips shorter than 1 s, so shorter ones are padded with silence to this length.</summary>
    private const int MinSamples = Segmenter.SampleRate * 11 / 10;

    private readonly WhisperFactory _factory;
    private readonly int _threads;
    private readonly SemaphoreSlim _gate = new(1, 1);
    // One processor per (language, translate, audio-context size): the context size is fixed when a processor is built.
    private readonly Dictionary<(string Language, bool Translate, int AudioCtx), WhisperProcessor> _processors = new();
    private readonly bool _adaptiveContext;
    private bool _disposed;

    /// <summary>
    /// Pick the native runtime. Call once, before the first engine is created.
    /// <paramref name="nativeAnchorFile"/> is a *file* path: Whisper.net looks for <c>runtimes\win-x64</c> next to it
    /// (SPEC §3.8). Null keeps the default probing (next to the app).
    /// </summary>
    public static void ConfigureRuntime(string? nativeAnchorFile, SttBackend backend)
    {
        if (nativeAnchorFile != null) RuntimeOptions.LibraryPath = nativeAnchorFile;
        RuntimeOptions.RuntimeLibraryOrder = backend switch
        {
            SttBackend.Vulkan => new List<RuntimeLibrary> { RuntimeLibrary.Vulkan, RuntimeLibrary.Cpu, RuntimeLibrary.CpuNoAvx },
            SttBackend.Cuda => new List<RuntimeLibrary> { RuntimeLibrary.Cuda, RuntimeLibrary.Cuda12, RuntimeLibrary.Cpu, RuntimeLibrary.CpuNoAvx },
            _ => new List<RuntimeLibrary> { RuntimeLibrary.Cpu, RuntimeLibrary.CpuNoAvx },
        };
    }

    /// <summary>Whisper's encoder always works on a 30 s window; the ladder below shrinks it to fit short clips (SPEC §6.1).</summary>
    private static readonly int[] AudioCtxLadder = { 250, 500, 1000, 1500 }; // 5 s, 10 s, 20 s, 30 s of mel frames / 2

    /// <summary>
    /// Upper bound on tokens Whisper may generate for a clip that fits an audio context. Speech is ~4-6 tokens/s; this allows
    /// ~10 plus a margin. Without it the decoder can loop on humming, singing or tones and burn every core for 10+ seconds.
    /// </summary>
    public static int MaxTokensFor(int audioCtx) => Math.Min(224, 20 + audioCtx / 5); // 250 -> 70, 500 -> 120, 1000+ -> 224

    /// <summary>
    /// Hard deadline for one call: 1 s plus half the clip length, never under 2 s. Normal speech takes ~0.3-0.8 s; this is the
    /// backstop for decoder runaways that the token cap alone doesn't end quickly. Whisper stops within milliseconds of a cancel.
    /// </summary>
    public static TimeSpan DeadlineFor(int samples) =>
        TimeSpan.FromSeconds(Math.Max(2.0, 1.0 + 0.5 * samples / Segmenter.SampleRate));

    /// <summary>Smallest audio context (units of 20 ms of audio) that still covers the clip plus half a second of margin.</summary>
    public static int AudioContextFor(int samples)
    {
        double needed = (samples / (double)Segmenter.SampleRate + 0.5) * 50.0;
        foreach (int ctx in AudioCtxLadder) if (needed <= ctx) return ctx;
        return AudioCtxLadder[^1];
    }

    /// <param name="adaptiveContext">Shrink the encoder window to the clip length: several times faster for short utterances.</param>
    public WhisperEngine(string modelPath, int threads, bool useGpu = false, bool adaptiveContext = true)
    {
        _adaptiveContext = adaptiveContext;
        _threads = Math.Max(1, threads);
        _factory = WhisperFactory.FromPath(modelPath, new WhisperFactoryOptions { UseGpu = useGpu });
    }

    /// <summary>The native runtime that was actually loaded (null before the first model load).</summary>
    public static RuntimeLibrary? LoadedRuntime => RuntimeOptions.LoadedLibrary;

    public async Task<Transcription> TranscribeAsync(float[] samples, TranscribeRequest request, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            float[] input = samples;
            if (input.Length < MinSamples)
            {
                input = new float[MinSamples];
                samples.CopyTo(input, 0);
            }

            int ctx = _adaptiveContext ? AudioContextFor(input.Length) : AudioCtxLadder[^1];
            var key = (request.Language, request.Translate, ctx);
            if (!_processors.TryGetValue(key, out var processor))
            {
                var b = _factory.CreateBuilder()
                    .WithLanguage(request.Language)
                    .WithThreads(_threads)
                    .WithNoContext();
                if (ctx < AudioCtxLadder[^1]) b = b.WithAudioContextSize(ctx);
                b = b.WithMaxTokensPerSegment(MaxTokensFor(ctx))
                     .WithTemperatureInc(0f); // no retries at higher temperature: each retry re-runs the whole decode
                if (request.Translate) b = b.WithTranslate();
                processor = b.Build();
                _processors[key] = processor;
            }

            var sw = Stopwatch.StartNew();
            var text = new StringBuilder();
            string language = "";
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(DeadlineFor(input.Length));
            try
            {
                await foreach (var seg in processor.ProcessAsync(input, deadline.Token).ConfigureAwait(false))
                {
                    text.Append(seg.Text);
                    if (language.Length == 0) language = seg.Language ?? "";
                }
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                return new Transcription("", "", sw.Elapsed, TimedOut: true); // our deadline, not the caller's cancel
            }
            return new Transcription(text.ToString().Trim(), language, sw.Elapsed);
        }
        finally { _gate.Release(); }
    }

    public void Dispose()
    {
        _gate.Wait();
        try
        {
            if (_disposed) return;
            _disposed = true;
            // DisposeAsync waits for a cancelled run to wind down; plain Dispose throws "Cannot dispose while processing".
            foreach (var p in _processors.Values)
            {
                try { p.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
                catch { /* shutting down: nothing useful to do */ }
            }
            _processors.Clear();
            _factory.Dispose();
        }
        finally { _gate.Release(); }
    }
}
