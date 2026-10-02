using System.Net;
using System.Security.Cryptography;
using System.Text;
using LupaFiscal.Cli;
using LupaFiscal.Core.Embeddings;
using LupaFiscal.Tests.Support;

namespace LupaFiscal.Tests;

public sealed class ModelDownloaderTests : IDisposable
{
    private static readonly byte[] ModelBytes = Encoding.UTF8.GetBytes("fake onnx model bytes");
    private static readonly byte[] PiecesBytes = Encoding.UTF8.GetBytes("fake sentencepiece model");

    private readonly TempDirectory _temp = new();
    private readonly ModelSpec _spec = new("test/model", "0123456789abcdef0123456789abcdef01234567", "test-model",
    [
        new ModelFile("onnx/model.onnx", "model.onnx", ModelBytes.Length, Sha(ModelBytes)),
        new ModelFile("onnx/sentencepiece.bpe.model", "sentencepiece.bpe.model", PiecesBytes.Length, Sha(PiecesBytes)),
    ]);

    public void Dispose() => _temp.Dispose();

    private static string Sha(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private ModelStore Store => new(Path.Combine(_temp.Path, "models"), _spec);

    private string Url(int file) => _spec.DownloadUri(_spec.Files[file]).AbsoluteUri;

    private static async Task<ModelDownloadSummary> Ensure(RecordingHandler handler, ModelStore store) =>
        await new ModelDownloader(handler, TextWriter.Null).EnsureAsync(store, CancellationToken.None);

    [Fact]
    public async Task DownloadsThePinnedRevisionFollowingCdnRedirectsWithoutCredentials()
    {
        var handler = new RecordingHandler()
            .On(Url(0), () => RecordingHandler.Redirect("https://cas-bridge.xethub.hf.co/xet/abc?sig=1"))
            .On("https://cas-bridge.xethub.hf.co/xet/abc?sig=1", () => RecordingHandler.Bytes(ModelBytes))
            .On(Url(1), () => RecordingHandler.Bytes(PiecesBytes));

        var summary = await Ensure(handler, Store);

        Assert.Equal(2, summary.Downloaded);
        Assert.Equal("https://huggingface.co/test/model/resolve/0123456789abcdef0123456789abcdef01234567/onnx/model.onnx", Url(0));
        Assert.Equal(ModelBytes, File.ReadAllBytes(Store.PathOf(_spec.Files[0])));
        Assert.Equal(PiecesBytes, File.ReadAllBytes(Store.PathOf(_spec.Files[1])));
        Assert.All(handler.Requests, r =>
        {
            Assert.Equal("https", r.Url.Scheme);
            Assert.Null(r.Authorization);
            Assert.False(r.HasCookie);
        });
        Store.VerifyAll();
    }

    [Fact]
    public async Task AFileWithTheWrongChecksumIsDiscardedAndTheDownloadFails()
    {
        var tampered = (byte[])ModelBytes.Clone();
        tampered[0] ^= 1;
        var handler = new RecordingHandler()
            .On(Url(0), () => RecordingHandler.Bytes(tampered))
            .On(Url(1), () => RecordingHandler.Bytes(PiecesBytes));

        var error = await Assert.ThrowsAsync<ModelIntegrityException>(() => Ensure(handler, Store));

        Assert.Contains("SHA-256", error.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(Store.PathOf(_spec.Files[0])));
        Assert.Empty(Directory.GetFiles(Store.DirectoryPath, "*.partial"));
        Assert.Throws<ModelIntegrityException>(Store.VerifyAll);
    }

    [Fact]
    public async Task ATruncatedOrOversizedResponseIsRejected()
    {
        var handler = new RecordingHandler()
            .On(Url(0), () => RecordingHandler.Bytes([.. ModelBytes, .. ModelBytes]));

        await Assert.ThrowsAsync<ModelIntegrityException>(() => Ensure(handler, Store));
        Assert.False(File.Exists(Store.PathOf(_spec.Files[0])));
    }

    [Theory]
    [InlineData("http://huggingface.co/plain-http")]
    [InlineData("https://evil.example.com/model.onnx")]
    [InlineData("https://huggingface.co.evil.example/model.onnx")]
    [InlineData("https://user:pass@cdn.hf.co/model.onnx")]
    [InlineData("https://cdn.hf.co:8443/model.onnx")]
    public async Task RedirectsOutsideHuggingFaceHttpsAreRefused(string target)
    {
        var handler = new RecordingHandler().On(Url(0), () => RecordingHandler.Redirect(target));

        await Assert.ThrowsAsync<ModelIntegrityException>(() => Ensure(handler, Store));

        Assert.Single(handler.Requests);
        Assert.False(File.Exists(Store.PathOf(_spec.Files[0])));
    }

    [Fact]
    public async Task VerifiedFilesAreKeptAndAnAlteredFileIsDownloadedAgain()
    {
        Directory.CreateDirectory(Store.DirectoryPath);
        File.WriteAllBytes(Store.PathOf(_spec.Files[0]), ModelBytes);
        File.WriteAllBytes(Store.PathOf(_spec.Files[1]), Encoding.UTF8.GetBytes("altered sentencepiece!!"));
        var handler = new RecordingHandler().On(Url(1), () => RecordingHandler.Bytes(PiecesBytes));

        var summary = await Ensure(handler, Store);

        Assert.Equal((1, 1), (summary.Downloaded, summary.AlreadyValid));
        Assert.Equal([Url(1)], handler.Requests.Select(r => r.Url.AbsoluteUri));
        Store.VerifyAll();
    }

    [Fact]
    public void VerifyAllRefusesAnAlteredFileBeforeUse()
    {
        Directory.CreateDirectory(Store.DirectoryPath);
        File.WriteAllBytes(Store.PathOf(_spec.Files[0]), ModelBytes);
        var altered = (byte[])PiecesBytes.Clone();
        altered[^1] ^= 1;
        File.WriteAllBytes(Store.PathOf(_spec.Files[1]), altered);

        var error = Assert.Throws<ModelIntegrityException>(Store.VerifyAll);

        Assert.Contains("does not match its pinned SHA-256", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ThePinnedModelIsTheMitLicensedE5SmallAtAFixedCommit()
    {
        var spec = ModelSpec.MultilingualE5Small;

        Assert.Equal("intfloat/multilingual-e5-small", spec.Repository);
        Assert.Matches("^[0-9a-f]{40}$", spec.Revision);
        Assert.Equal(["model.onnx", "sentencepiece.bpe.model", "tokenizer.json"], spec.Files.Select(f => f.LocalName));
        Assert.All(spec.Files, f => Assert.Matches("^[0-9a-f]{64}$", f.Sha256));
        Assert.All(spec.Files, f => Assert.StartsWith($"https://huggingface.co/intfloat/multilingual-e5-small/resolve/{spec.Revision}/onnx/",
            spec.DownloadUri(f).AbsoluteUri, StringComparison.Ordinal));
    }

    [Fact]
    public async Task ModelDownloadCommandReportsAFailedChecksumWithExitOne()
    {
        // The CLI uses the real pins; a fake server returning other bytes must make it fail.
        var dataDir = _temp.Path;
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var handler = new RecordingHandler();
        foreach (var file in ModelSpec.MultilingualE5Small.Files)
        {
            handler.On(ModelSpec.MultilingualE5Small.DownloadUri(file).AbsoluteUri, () => RecordingHandler.Bytes([1, 2, 3]));
        }

        var exit = await CliApp.RunAsync(["model", "download", "--data-dir", dataDir], stdout, stderr, CancellationToken.None, () => handler);

        Assert.Equal(CliApp.ExitFailure, exit);
        Assert.Contains("model.onnx", stderr.ToString(), StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(Path.Combine(dataDir, "models"), "*", SearchOption.AllDirectories));
    }

    [Fact]
    public void TheEmbedderLoadsNothingWhenAFileFailsItsChecksum()
    {
        var store = new ModelStore(Path.Combine(_temp.Path, "models"), ModelSpec.MultilingualE5Small);
        Directory.CreateDirectory(store.DirectoryPath);
        foreach (var file in store.Spec.Files) File.WriteAllBytes(store.PathOf(file), [0, 1, 2]);

        var error = Assert.Throws<ModelIntegrityException>(() => E5Embedder.Load(store));

        Assert.Contains("Run: model download", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SearchWithoutAVerifiedModelFailsWithExitOne()
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exit = await CliApp.RunAsync(["search", "IRS", "--data-dir", _temp.Path], stdout, stderr, CancellationToken.None);

        Assert.Equal(CliApp.ExitFailure, exit);
        Assert.Contains("Run: model download", stderr.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheFirstIndexDownloadsTheModelAndStopsWhenItFailsVerification()
    {
        TestCorpus.Write(_temp.Path, TestCorpus.All);
        var handler = new RecordingHandler();
        foreach (var file in ModelSpec.MultilingualE5Small.Files)
        {
            handler.On(ModelSpec.MultilingualE5Small.DownloadUri(file).AbsoluteUri, () => RecordingHandler.Bytes([1, 2, 3]));
        }

        var exit = await CliApp.RunAsync(["index", "--data-dir", _temp.Path], new StringWriter(), new StringWriter(),
            CancellationToken.None, () => handler);

        Assert.Equal(CliApp.ExitFailure, exit);
        Assert.Equal(ModelSpec.MultilingualE5Small.DownloadUri(ModelSpec.MultilingualE5Small.Files[0]), handler.Requests[0].Url);
        Assert.False(File.Exists(Path.Combine(_temp.Path, "lupa-fiscal.db")));
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, Func<HttpResponseMessage>> _responses = new(StringComparer.Ordinal);

        public List<(Uri Url, string? Authorization, bool HasCookie)> Requests { get; } = [];

        public RecordingHandler On(string url, Func<HttpResponseMessage> response)
        {
            _responses[url] = response;
            return this;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.RequestUri!, request.Headers.Authorization?.ToString(), request.Headers.Contains("Cookie")));
            var response = _responses.TryGetValue(request.RequestUri!.AbsoluteUri, out var make)
                ? make()
                : new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("") };
            response.RequestMessage = request;
            return Task.FromResult(response);
        }

        public static HttpResponseMessage Bytes(byte[] body) => FakeHandler.Bytes(body, "application/octet-stream");

        public static HttpResponseMessage Redirect(string location) => FakeHandler.Redirect(location);
    }
}
