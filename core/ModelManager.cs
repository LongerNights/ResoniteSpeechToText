using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace SpeechToText;

/// <param name="ExpectedSize">Exact byte size when known; otherwise only the server's Content-Length / hash is checked.</param>
public sealed record ModelFile(string FileName, string Url, long? ExpectedSize = null);

/// <summary>Where each model lives (SPEC §6.6). Whisper models are ggml files from ggerganov/whisper.cpp on Hugging Face.</summary>
public static class ModelCatalog
{
    private const string WhisperBase = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/";

    public static readonly ModelFile SileroVad = new(
        "ggml-silero-v5.1.2.bin", "https://huggingface.co/ggml-org/whisper-vad/resolve/main/ggml-silero-v5.1.2.bin", 885_098);

    public static ModelFile Get(SttModel model) => model switch
    {
        SttModel.TinyEn => new("ggml-tiny.en.bin", WhisperBase + "ggml-tiny.en.bin"),
        SttModel.BaseEn => new("ggml-base.en.bin", WhisperBase + "ggml-base.en.bin", 147_964_211),
        SttModel.SmallEn => new("ggml-small.en.bin", WhisperBase + "ggml-small.en.bin"),
        SttModel.Base => new("ggml-base.bin", WhisperBase + "ggml-base.bin"),
        SttModel.Small => new("ggml-small.bin", WhisperBase + "ggml-small.bin"),
        SttModel.LargeV3Turbo => new("ggml-large-v3-turbo.bin", WhisperBase + "ggml-large-v3-turbo.bin"),
        _ => throw new ArgumentOutOfRangeException(nameof(model)),
    };

    /// <summary>Rough download size in megabytes, for the "downloading ..." notice.</summary>
    public static int ApproxMegabytes(SttModel model) => model switch
    {
        SttModel.TinyEn => 75,
        SttModel.BaseEn => 148,
        SttModel.SmallEn => 488,
        SttModel.Base => 148,
        SttModel.Small => 488,
        SttModel.LargeV3Turbo => 1620,
        _ => 0,
    };

    public static bool IsEnglishOnly(SttModel model) => model is SttModel.TinyEn or SttModel.BaseEn or SttModel.SmallEn;
}

/// <summary>
/// Downloads models to a local folder: to a <c>.part</c> file first, size- and SHA-256-checked, then renamed, so a
/// half-finished or corrupt download is never mistaken for an installed model. Hugging Face announces the file's SHA-256
/// in the <c>X-Linked-ETag</c> header of its redirect, so redirects are followed by hand to read it.
/// </summary>
public sealed class ModelManager : IDisposable
{
    private const int MaxRedirects = 6;
    private readonly string _dir;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly Dictionary<string, SemaphoreSlim> _locks = new();

    /// <param name="handler">Test seam. Must not follow redirects itself; null creates a real client.</param>
    public ModelManager(string modelsDirectory, HttpMessageHandler? handler = null)
    {
        _dir = modelsDirectory;
        _ownsHttp = true;
        _http = handler != null
            ? new HttpClient(handler, disposeHandler: false)
            : new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.None });
        _http.Timeout = TimeSpan.FromMinutes(30);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("SpeechToText-ResoniteMod/0.1");
    }

    /// <summary>
    /// After a download: whether the server announced a SHA-256 and the file matched it (false = no hash was offered, so
    /// only the size checks applied). Null until something has been downloaded.
    /// </summary>
    public bool? LastDownloadWasHashVerified { get; private set; }

    public string PathFor(ModelFile file) => Path.Combine(_dir, file.FileName);

    public bool IsInstalled(ModelFile file)
    {
        var info = new FileInfo(PathFor(file));
        return info.Exists && info.Length > 0 && (file.ExpectedSize == null || info.Length == file.ExpectedSize);
    }

    /// <summary>Returns the local path, downloading first if the model isn't installed.</summary>
    public async Task<string> EnsureAsync(ModelFile file, IProgress<(long Done, long Total)>? progress = null, CancellationToken ct = default)
    {
        string path = PathFor(file);
        SemaphoreSlim gate;
        lock (_locks) { if (!_locks.TryGetValue(file.FileName, out gate!)) _locks[file.FileName] = gate = new SemaphoreSlim(1, 1); }
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (IsInstalled(file)) return path;
            Directory.CreateDirectory(_dir);
            string part = path + ".part";
            try
            {
                await DownloadAsync(file, part, progress, ct).ConfigureAwait(false);
                File.Move(part, path, overwrite: true);
            }
            catch
            {
                try { File.Delete(part); } catch { /* best effort */ }
                throw;
            }
            return path;
        }
        finally { gate.Release(); }
    }

    private async Task DownloadAsync(ModelFile file, string partPath, IProgress<(long, long)>? progress, CancellationToken ct)
    {
        string url = file.Url;
        string? sha = null;
        HttpResponseMessage resp;
        for (int hop = 0; ; hop++)
        {
            resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (resp.Headers.TryGetValues("X-Linked-ETag", out var etag))
            {
                string v = string.Concat(etag).Trim('"');
                if (v.Length == 64) sha = v;
            }
            if ((int)resp.StatusCode is >= 300 and < 400 && resp.Headers.Location != null)
            {
                if (hop >= MaxRedirects) { resp.Dispose(); throw new HttpRequestException("Too many redirects for " + file.Url); }
                url = new Uri(new Uri(url), resp.Headers.Location).ToString();
                resp.Dispose();
                continue;
            }
            break;
        }

        using (resp)
        {
            resp.EnsureSuccessStatusCode();
            long? total = resp.Content.Headers.ContentLength;
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long done = 0;
            await using (var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var dst = new FileStream(partPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
            {
                var buf = new byte[1 << 16];
                int n;
                while ((n = await src.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
                    hash.AppendData(buf, 0, n);
                    done += n;
                    progress?.Report((done, total ?? file.ExpectedSize ?? -1));
                }
            }

            if (total != null && done != total) throw new InvalidDataException($"Incomplete download: got {done} of {total} bytes.");
            if (file.ExpectedSize != null && done != file.ExpectedSize) throw new InvalidDataException($"Unexpected size {done}, expected {file.ExpectedSize}.");
            if (sha != null)
            {
                string actual = Convert.ToHexString(hash.GetHashAndReset());
                if (!actual.Equals(sha, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("SHA-256 mismatch for " + file.FileName);
            }
            LastDownloadWasHashVerified = sha != null;
        }
    }

    public void Dispose() { if (_ownsHttp) _http.Dispose(); }
}
