using System.Net;
using System.Security.Cryptography;

namespace SpeechToText.Tests;

public class ModelManagerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "stt-models-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private sealed class FakeHandler : HttpMessageHandler
    {
        public int Requests;
        public Func<HttpRequestMessage, HttpResponseMessage> Respond = _ => new HttpResponseMessage(HttpStatusCode.NotFound);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Requests);
            return Task.FromResult(Respond(request));
        }
    }

    private static readonly byte[] Payload = Enumerable.Range(0, 200_000).Select(i => (byte)(i * 31 + 7)).ToArray();
    private static string Sha(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();

    /// <summary>Mimics Hugging Face: a 302 carrying X-Linked-ETag, then the file from a "CDN" URL.</summary>
    private static FakeHandler HuggingFaceLike(byte[] body, string? sha, long? lieAboutLength = null)
    {
        var h = new FakeHandler();
        h.Respond = req =>
        {
            if (req.RequestUri!.Host == "huggingface.co")
            {
                var redirect = new HttpResponseMessage(HttpStatusCode.Found);
                redirect.Headers.Location = new Uri("https://cdn.example/blob/abc");
                if (sha != null) redirect.Headers.Add("X-Linked-ETag", $"\"{sha}\"");
                return redirect;
            }
            var ok = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };
            if (lieAboutLength != null) ok.Content.Headers.ContentLength = lieAboutLength;
            return ok;
        };
        return h;
    }

    private static readonly ModelFile File1 = new("model.bin", "https://huggingface.co/x/y/resolve/main/model.bin");

    [Fact]
    public async Task DownloadsFollowsRedirectsAndVerifiesTheHash()
    {
        var h = HuggingFaceLike(Payload, Sha(Payload));
        using var mm = new ModelManager(_dir, h);
        var progress = new List<(long, long)>();
        string path = await mm.EnsureAsync(File1, new Progress<(long, long)>(p => { lock (progress) progress.Add(p); }));

        Assert.Equal(Payload, await File.ReadAllBytesAsync(path));
        Assert.False(File.Exists(path + ".part"));
        Assert.True(mm.IsInstalled(File1));
        Assert.Equal(2, h.Requests); // redirect + file
        await Task.Delay(50); // Progress<T> posts to the thread pool
        lock (progress) { Assert.NotEmpty(progress); Assert.Equal(Payload.Length, progress[^1].Item1); }
    }

    [Fact]
    public async Task SecondCallUsesTheInstalledFileWithoutTouchingTheNetwork()
    {
        var h = HuggingFaceLike(Payload, Sha(Payload));
        using var mm = new ModelManager(_dir, h);
        await mm.EnsureAsync(File1);
        int after = h.Requests;
        await mm.EnsureAsync(File1);
        Assert.Equal(after, h.Requests);
    }

    [Fact]
    public async Task HashMismatchFailsAndLeavesNothingBehind()
    {
        var h = HuggingFaceLike(Payload, new string('a', 64));
        using var mm = new ModelManager(_dir, h);
        await Assert.ThrowsAsync<InvalidDataException>(() => mm.EnsureAsync(File1));
        Assert.False(File.Exists(mm.PathFor(File1)));
        Assert.False(File.Exists(mm.PathFor(File1) + ".part"));
        Assert.False(mm.IsInstalled(File1));
    }

    [Fact]
    public async Task TruncatedDownloadIsRejected()
    {
        var h = HuggingFaceLike(Payload, null, lieAboutLength: Payload.Length + 10);
        using var mm = new ModelManager(_dir, h);
        await Assert.ThrowsAsync<InvalidDataException>(() => mm.EnsureAsync(File1));
        Assert.False(File.Exists(mm.PathFor(File1)));
        Assert.False(File.Exists(mm.PathFor(File1) + ".part"));
    }

    [Fact]
    public async Task WrongExpectedSizeIsRejected()
    {
        var h = HuggingFaceLike(Payload, null);
        using var mm = new ModelManager(_dir, h);
        var file = File1 with { ExpectedSize = Payload.Length + 1 };
        await Assert.ThrowsAsync<InvalidDataException>(() => mm.EnsureAsync(file));
        Assert.False(File.Exists(mm.PathFor(file)));
    }

    [Fact]
    public async Task HttpErrorsSurfaceAndLeaveNothingBehind()
    {
        var h = new FakeHandler(); // everything 404
        using var mm = new ModelManager(_dir, h);
        await Assert.ThrowsAsync<HttpRequestException>(() => mm.EnsureAsync(File1));
        Assert.False(File.Exists(mm.PathFor(File1) + ".part"));
    }

    [Fact]
    public async Task ConcurrentCallsDownloadOnce()
    {
        var h = HuggingFaceLike(Payload, Sha(Payload));
        using var mm = new ModelManager(_dir, h);
        await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => mm.EnsureAsync(File1)));
        Assert.Equal(2, h.Requests);
    }

    [Fact]
    public void ACorruptLeftoverOfTheWrongSizeIsNotTreatedAsInstalled()
    {
        using var mm = new ModelManager(_dir, new FakeHandler());
        Directory.CreateDirectory(_dir);
        var file = File1 with { ExpectedSize = 100 };
        File.WriteAllBytes(mm.PathFor(file), new byte[50]);
        Assert.False(mm.IsInstalled(file));
    }

    [Fact]
    public void CatalogCoversEveryModel()
    {
        foreach (SttModel m in Enum.GetValues<SttModel>())
        {
            var f = ModelCatalog.Get(m);
            Assert.StartsWith("https://huggingface.co/ggerganov/whisper.cpp/resolve/main/", f.Url);
            Assert.EndsWith(".bin", f.FileName);
        }
        Assert.True(ModelCatalog.IsEnglishOnly(SttModel.BaseEn));
        Assert.False(ModelCatalog.IsEnglishOnly(SttModel.Base));
    }

    [Fact]
    public async Task HashIsReportedAsVerifiedOnlyWhenTheServerOfferedOne()
    {
        using (var mm = new ModelManager(_dir, HuggingFaceLike(Payload, Sha(Payload))))
        {
            await mm.EnsureAsync(File1);
            Assert.True(mm.LastDownloadWasHashVerified);
        }
        using (var mm = new ModelManager(_dir, HuggingFaceLike(Payload, null)))
        {
            await mm.EnsureAsync(File1 with { FileName = "other.bin" });
            Assert.False(mm.LastDownloadWasHashVerified);
        }
    }

    [NetworkFact]
    public async Task OptIn_RealHuggingFaceDownload()
    {
        // Talks to the real server (885 KB). Run with: STT_NETWORK_TESTS=1 dotnet test --filter OptIn_
        using var mm = new ModelManager(_dir);
        string path = await mm.EnsureAsync(ModelCatalog.SileroVad);
        Assert.Equal(ModelCatalog.SileroVad.ExpectedSize, new FileInfo(path).Length);
        Assert.True(mm.LastDownloadWasHashVerified, "Hugging Face did not announce a SHA-256 via X-Linked-ETag on the redirect");
        // Identical to the copy fetched with curl in M1.
        string known = Repo.Model(ModelCatalog.SileroVad.FileName);
        if (File.Exists(known)) Assert.Equal(await File.ReadAllBytesAsync(known), await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public void RealModelFilesInTheRepoHaveTheCatalogSizes()
    {
        // The two files we know exactly, downloaded in M1 from the same URLs. Skips quietly if they aren't there.
        foreach (var f in new[] { ModelCatalog.Get(SttModel.BaseEn), ModelCatalog.SileroVad })
        {
            string p = Repo.Model(f.FileName);
            if (File.Exists(p)) Assert.Equal(f.ExpectedSize, new FileInfo(p).Length);
        }
    }
}
