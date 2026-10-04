using System.Security.Cryptography;
using System.Text;
using LupaFiscal.Cli;
using LupaFiscal.Core.Answering;
using LupaFiscal.Core.Embeddings;
using LupaFiscal.Tests.Support;

namespace LupaFiscal.Tests;

/// <summary>Runs only when the default answer model is in data/models (never in CI, which does not download it).</summary>
public sealed class RealAnswerModelFactAttribute : FactAttribute
{
    public RealAnswerModelFactAttribute()
    {
        if (RealAnswerModel.Store is null) Skip = $"Answer model not found in data/models; run: model download --model {AnswerCommands.DefaultModel.Name}";
    }
}

internal static class RealAnswerModel
{
    public static ModelStore? Store { get; } = Find();

    private static ModelStore? Find()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (!File.Exists(Path.Combine(directory.FullName, "LupaFiscal.slnx"))) continue;
            var store = new ModelStore(Path.Combine(directory.FullName, "data", "models"), AnswerCommands.DefaultModel.Spec);
            return store.AllFilesPresent ? store : null;
        }
        return null;
    }
}

public sealed class AnswerModelTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    public static TheoryData<string> Models() => [.. AnswerModel.All.Select(m => m.Name)];

    [Theory]
    [MemberData(nameof(Models))]
    public void EveryCandidateIsPinnedToACommitWithSizesAndSha256(string name)
    {
        var model = AnswerModel.Find(name)!;
        var spec = model.Spec;

        Assert.Matches("^[0-9a-f]{40}$", spec.Revision);
        Assert.Matches("^[a-z0-9.-]+$", spec.DirectoryName);
        Assert.All(spec.Files, f => Assert.Matches("^[0-9a-f]{64}$", f.Sha256));
        Assert.All(spec.Files, f => Assert.True(f.Size > 0));
        Assert.Equal(spec.Files.Count, spec.Files.Select(f => f.LocalName).Distinct().Count());
        Assert.Subset(spec.Files.Select(f => f.LocalName).ToHashSet(),
            new HashSet<string> { "genai_config.json", "model.onnx", "model.onnx.data", "tokenizer.json", "tokenizer_config.json" });
        Assert.All(spec.Files, f =>
        {
            var uri = spec.DownloadUri(f);
            Assert.StartsWith($"https://huggingface.co/{spec.Repository}/resolve/{spec.Revision}/", uri.AbsoluteUri, StringComparison.Ordinal);
            Assert.True(ModelDownloader.IsAllowed(uri));
            Assert.EndsWith("/" + f.LocalName, uri.AbsolutePath, StringComparison.Ordinal);
        });
        Assert.False(string.IsNullOrWhiteSpace(model.Licence));
        Assert.Matches(@"^(MIT|Apache-2\.0)\b", model.Licence);
        Assert.True(model.ContextTokens >= AnswerCommands.Options.Prompt.PromptTokenBudget + AnswerCommands.Options.Prompt.MaxNewTokens);
        Assert.Same(model, AnswerModel.Find(name.ToUpperInvariant()));
    }

    [Fact]
    public void TheCandidatesAreTheThreePermissiveModelsAndTheDefaultIsOneOfThem()
    {
        Assert.Equal(["qwen3-4b", "phi-4-mini", "eurollm-9b"], AnswerModel.All.Select(m => m.Name));
        Assert.Equal(["Apache-2.0", "MIT"], AnswerModel.All.Select(m => m.Licence.Split(' ')[0]).Distinct().Order());
        Assert.Contains(AnswerCommands.DefaultModel, AnswerModel.All);
        Assert.Null(AnswerModel.Find("llama-3"));
    }

    [Fact]
    public async Task AnAnswerModelDownloadsIntoItsOwnFolderAndIsVerified()
    {
        var bytes = Encoding.UTF8.GetBytes("{\"model\":{}}");
        var spec = new ModelSpec("test/answer", "0123456789abcdef0123456789abcdef01234567", "test-answer",
            [new ModelFile("cpu/genai_config.json", "genai_config.json", bytes.Length, Convert.ToHexStringLower(SHA256.HashData(bytes)))]);
        var store = new ModelStore(Path.Combine(_temp.Path, "models"), spec);
        var handler = new FakeHandler(new FakeClock()).Always(spec.DownloadUri(spec.Files[0]).AbsoluteUri,
                () => FakeHandler.Redirect("https://cas-bridge.xethub.hf.co/xet/answer?sig=1"))
            .Always("https://cas-bridge.xethub.hf.co/xet/answer?sig=1", () => FakeHandler.Bytes(bytes, "application/octet-stream"));

        using var downloader = new ModelDownloader(handler, TextWriter.Null);
        var summary = await downloader.EnsureAsync(store, CancellationToken.None);

        Assert.Equal(1, summary.Downloaded);
        Assert.Equal(Path.Combine(_temp.Path, "models", "test-answer", "genai_config.json"), store.PathOf(spec.Files[0]));
        store.VerifyAll();
    }

    [Fact]
    public async Task ModelDownloadOfAnAnswerModelRefusesBytesThatDoNotMatchThePins()
    {
        var model = AnswerModel.Qwen3_4B;
        var handler = new FakeHandler(new FakeClock());
        foreach (var file in model.Spec.Files) handler.Always(model.Spec.DownloadUri(file).AbsoluteUri, () => FakeHandler.Bytes([1, 2, 3], "application/octet-stream"));
        var stderr = new StringWriter();

        var exit = await CliApp.RunAsync(["model", "download", "--model", "qwen3-4b", "--data-dir", _temp.Path], new StringWriter(), stderr,
            CancellationToken.None, () => handler);

        Assert.Equal(CliApp.ExitFailure, exit);
        Assert.Contains("genai_config.json", stderr.ToString(), StringComparison.Ordinal);
        Assert.Equal(model.Spec.DownloadUri(model.Spec.Files[0]), handler.Requests.Single().Url);
        Assert.Empty(Directory.GetFiles(Path.Combine(_temp.Path, "models"), "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task ModelDownloadRejectsAnUnknownAnswerModel()
    {
        var stderr = new StringWriter();

        var exit = await CliApp.RunAsync(["model", "download", "--model", "gpt-4", "--data-dir", _temp.Path], new StringWriter(), stderr,
            CancellationToken.None, () => throw new InvalidOperationException("no network in tests"));

        Assert.Equal(CliApp.ExitUsage, exit);
        Assert.Contains("Unknown answer model 'gpt-4' (available: qwen3-4b, phi-4-mini, eurollm-9b)", stderr.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void TheGeneratorLoadsNothingWhenAFileIsMissing()
    {
        var store = new ModelStore(Path.Combine(_temp.Path, "models"), AnswerModel.Qwen3_4B.Spec);

        var error = Assert.Throws<ModelIntegrityException>(() => OnnxGenAiGenerator.Load(store, AnswerModel.Qwen3_4B));

        Assert.Contains("Model file missing", error.Message, StringComparison.Ordinal);
        Assert.EndsWith("Run: model download --model qwen3-4b", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheGeneratorLoadsNothingWhenAFileIsTampered()
    {
        var store = new ModelStore(Path.Combine(_temp.Path, "models"), AnswerModel.Phi4Mini.Spec);
        Directory.CreateDirectory(store.DirectoryPath);
        foreach (var file in store.Spec.Files) File.WriteAllBytes(store.PathOf(file), [0, 1, 2]);

        var error = Assert.Throws<ModelIntegrityException>(() => OnnxGenAiGenerator.Load(store, AnswerModel.Phi4Mini));

        Assert.Contains("does not match its pinned SHA-256; it was not used", error.Message, StringComparison.Ordinal);
        Assert.EndsWith("--model phi-4-mini", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheGeneratorRefusesAStoreOfAnotherModel()
    {
        var store = new ModelStore(Path.Combine(_temp.Path, "models"), AnswerModel.Phi4Mini.Spec);

        Assert.Throws<ArgumentException>(() => OnnxGenAiGenerator.Load(store, AnswerModel.Qwen3_4B));
    }

    [Theory]
    [InlineData("qwen3-4b", "<|im_start|>system\nS<|im_end|>\n<|im_start|>user\nU<|im_end|>\n<|im_start|>assistant\n")]
    [InlineData("eurollm-9b", "<|im_start|>system\nS<|im_end|>\n<|im_start|>user\nU<|im_end|>\n<|im_start|>assistant\n")]
    [InlineData("phi-4-mini", "<|system|>S<|end|><|user|>U<|end|><|assistant|>")]
    public void EachModelGetsItsChatTemplate(string name, string expected)
    {
        Assert.Equal(expected, OnnxGenAiGenerator.Format(AnswerModel.Find(name)!, new GenerationRequest("S", "U", 10, TimeSpan.FromSeconds(1))));
    }

    [RealAnswerModelFact]
    public void TheRealModelLoadsVerifiedAndGeneratesWithinItsTokenLimit()
    {
        using var generator = OnnxGenAiGenerator.Load(RealAnswerModel.Store!, AnswerCommands.DefaultModel);

        var output = generator.Generate(new GenerationRequest("Responde numa palavra.", "Qual é a capital de Portugal?", 8, TimeSpan.FromSeconds(60)),
            CancellationToken.None);

        Assert.Equal(AnswerCommands.DefaultModel.Id, generator.ModelId);
        Assert.InRange(output.GeneratedTokens, 1, 8);
        Assert.Contains("Lisboa", output.Text, StringComparison.OrdinalIgnoreCase);
        Assert.True(output.PromptTokens > generator.CountTokens("Qual é a capital de Portugal?"));
        Assert.True(output.FirstTokenMs > 0 && output.FirstTokenMs <= output.TotalMs);
    }

    [RealAnswerModelFact]
    public void TheRealModelStopsAtTheTimeLimitAndHonoursCancellation()
    {
        using var generator = OnnxGenAiGenerator.Load(RealAnswerModel.Store!, AnswerCommands.DefaultModel);
        var request = new GenerationRequest("Escreve um texto longo.", "Descreve o sistema fiscal português em pormenor.", 2000, TimeSpan.FromSeconds(3));

        var output = generator.Generate(request, CancellationToken.None);
        using var cancelled = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        Assert.Equal(GenerationStop.TimeLimit, output.Stop);
        Assert.InRange(output.TotalMs, 2_500, 20_000);
        Assert.ThrowsAny<OperationCanceledException>(() => generator.Generate(request with { TimeLimit = TimeSpan.FromMinutes(5) }, cancelled.Token));

        // A stopped generation leaves the model usable for the next one.
        var next = generator.Generate(new GenerationRequest("Responde numa palavra.", "Qual é a capital de Portugal?", 8, TimeSpan.FromSeconds(60)),
            CancellationToken.None);
        Assert.Contains("Lisboa", next.Text, StringComparison.OrdinalIgnoreCase);
    }
}
