using System.Security.Cryptography;
using System.Text.Json;
using LupaFiscal.Cli;
using LupaFiscal.Core.Answering;
using LupaFiscal.Core.Evaluation;
using LupaFiscal.Tests.Support;

namespace LupaFiscal.Tests;

public sealed class AnswerCliTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly List<string> _loadedModels = [];
    private Func<GenerationRequest, string>? _respond;

    public AnswerCliTests() => TestCorpus.Write(_temp.Path, TestCorpus.All);

    public void Dispose() => _temp.Dispose();

    private async Task<(int Exit, string Out, string Err)> Run(params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        // Fake embedder and generator: nothing is downloaded or loaded.
        var exit = await CliApp.RunAsync([.. args, "--data-dir", _temp.Path], stdout, stderr, CancellationToken.None,
            () => throw new InvalidOperationException("no network in tests"), () => new FakeEmbedder(), null,
            model =>
            {
                _loadedModels.Add(model.Name);
                return new FakeAnswerGenerator(_respond);
            });
        return (exit, stdout.ToString(), stderr.ToString());
    }

    private async Task Index() => Assert.Equal(CliApp.ExitOk, (await Run("index")).Exit);

    private string WriteQuestions(string name, params (string Id, string Question, string Expected)[] questions)
    {
        var path = Path.Combine(_temp.Path, name);
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            questions = questions.Select(q => new { id = q.Id, question = q.Question, expected = new[] { q.Expected } }),
        }));
        return path;
    }

    [Fact]
    public async Task AnswerPrintsTheValidatedSentencesTheirSourcesAndTimings()
    {
        await Index();
        _respond = request => "As despesas de educação são dedutíveis à coleta [1]. Isto é sempre verdade. Há outra regra [8].";

        var (exit, output, _) = await Run("answer", "Posso deduzir as despesas de educação dos filhos?");

        Assert.Equal(CliApp.ExitOk, exit);
        Assert.Equal(["qwen3-4b"], _loadedModels);
        Assert.Contains("Query: Posso deduzir as despesas de educação dos filhos?", output, StringComparison.Ordinal);
        Assert.Contains($"Model: qwen3-4b ({FakeAnswerGenerator.FakeModelId})", output, StringComparison.Ordinal);
        Assert.Contains("Resposta:\nAs despesas de educação são dedutíveis à coleta. [1]\n\nFontes:\n[1] processo ", output.ReplaceLineEndings("\n"),
            StringComparison.Ordinal);
        Assert.Matches(@"\[1\] processo \d+ \| CIRS art\. [\w-]+ \| \d{4}-\d{2}-\d{2} \| https://info\.portaldasfinancas\.gov\.pt/\S+", output);
        Assert.DoesNotContain("sempre verdade", output, StringComparison.Ordinal);
        Assert.DoesNotContain("[8]", output, StringComparison.Ordinal);
        Assert.Matches(@"Timings: search \d+ ms, first token \d+ ms, generation \d+ ms, total \d+ ms; \d+ token\(s\) generated, stop: end of text\.", output);
        Assert.Contains("Model output: 1 of 3 sentence(s) with valid citations before filtering.", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnswerPrintsTheRefusalWithoutSourcesWhenNothingIsCited()
    {
        await Index();
        _respond = _ => "Não encontrei a resposta nas fontes.";

        var (exit, output, _) = await Run("answer", "Qual é a taxa de IMI?", "--model", "phi-4-mini");

        Assert.Equal(CliApp.ExitOk, exit);
        Assert.Equal(["phi-4-mini"], _loadedModels);
        Assert.Contains("Resposta:\n" + CitationEnforcer.RefusalText + "\n", output.ReplaceLineEndings("\n"), StringComparison.Ordinal);
        Assert.DoesNotContain("Fontes:", output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(new[] { "answer", "IRS", "--article", "10" }, "--article needs --tax")]
    [InlineData(new[] { "answer", "IRS", "--year", "dois mil" }, "Option --year must be an integer.")]
    [InlineData(new[] { "answer", "IRS", "--model", "llama-3" }, "Unknown answer model 'llama-3'")]
    [InlineData(new[] { "answer", "IRS", "--limit", "5" }, "Unknown option(s): --limit.")]
    [InlineData(new[] { "answer" }, "Usage: answer \"question\"")]
    [InlineData(new[] { "answer-eval" }, "Usage: answer-eval --questions")]
    [InlineData(new[] { "answer-eval", "--questions", "q.json", "--sample", "0" }, "--sample must be at least 1.")]
    [InlineData(new[] { "answer-eval", "--questions", "q.json", "--out", "x.json" }, "--out names the recorded file; add --record.")]
    public async Task InvalidOptionsExitTwoBeforeLoadingAnything(string[] args, string message)
    {
        var (exit, _, error) = await Run(args);

        Assert.Equal(CliApp.ExitUsage, exit);
        Assert.Contains(message, error, StringComparison.Ordinal);
        Assert.Empty(_loadedModels);
    }

    [Fact]
    public async Task ArticleAndTaxFiltersAreAppliedLikeSearch()
    {
        await Index();
        GenerationRequest? seen = null;
        _respond = request =>
        {
            seen = request;
            return "O ganho na alienação é excluído de tributação [1].";
        };

        var (exit, output, _) = await Run("answer", "mais-valias na venda da casa", "--tax", "CIRS", "--article", "10");

        Assert.Equal(CliApp.ExitOk, exit);
        Assert.Contains("Passages in the prompt: 1 ", output, StringComparison.Ordinal);
        Assert.Contains("Processo 90002", seen!.User, StringComparison.Ordinal);
        Assert.Contains("[1] processo 90002 | CIRS art. 10 |", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnswerWithoutTheModelFailsWithTheDownloadCommand()
    {
        await Index();
        var stderr = new StringWriter();

        var exit = await CliApp.RunAsync(["answer", "IRS", "--data-dir", _temp.Path], new StringWriter(), stderr, CancellationToken.None,
            null, () => new FakeEmbedder());

        Assert.Equal(CliApp.ExitFailure, exit);
        Assert.Contains("Answer model qwen3-4b is not in", stderr.ToString(), StringComparison.Ordinal);
        Assert.Contains("Run: model download --model qwen3-4b", stderr.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnswerWithATamperedModelFailsBeforeLoadingIt()
    {
        await Index();
        var store = new Core.Embeddings.ModelStore(Path.Combine(_temp.Path, "models"), AnswerModel.Qwen3_4B.Spec);
        Directory.CreateDirectory(store.DirectoryPath);
        foreach (var file in store.Spec.Files) File.WriteAllText(store.PathOf(file), "{}");
        var stderr = new StringWriter();

        var exit = await CliApp.RunAsync(["answer", "IRS", "--data-dir", _temp.Path], new StringWriter(), stderr, CancellationToken.None,
            null, () => new FakeEmbedder());

        Assert.Equal(CliApp.ExitFailure, exit);
        Assert.Contains("does not match its pinned SHA-256; it was not used. Run: model download --model qwen3-4b", stderr.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnswerEvalPrintsTheMetricsPerModelAndWritesNothingWithoutRecord()
    {
        await Index();
        var cirs = WriteQuestions("cirs.json",
            ("q1", "Posso deduzir as despesas de educação dos filhos?", "piv_90001"),
            ("q2", "As rendas das partes comuns do condomínio são rendimentos prediais?", "piv_90003"),
            ("q3", "Pensões pagas por entidade estrangeira são tributadas?", "piv_99999"));
        var taxes = WriteQuestions("taxes.json", ("t1", "Mais-valias na venda da habitação própria com reinvestimento", "piv_90002"));
        var before = Snapshot();
        // Cites [1] (the best passage) for every question except the pension one, which gets no valid sentence.
        _respond = request => request.User.Contains("<pergunta>Pensões pagas", StringComparison.Ordinal)
            ? "Não sei responder. Talvez [7]."
            : FakeAnswerGenerator.CiteFirstSource(request);

        var (exit, output, _) = await Run("answer-eval", "--questions", $"{cirs},{taxes}", "--sample", "4", "--model", "qwen3-4b,phi-4-mini");

        Assert.Equal(CliApp.ExitOk, exit);
        Assert.Equal(["qwen3-4b", "phi-4-mini"], _loadedModels);
        Assert.Contains("4 question(s) sampled", output, StringComparison.Ordinal);
        Assert.Contains("(q1, q2, q3, t1)", output, StringComparison.Ordinal);
        Assert.Contains("Model qwen3-4b (", output, StringComparison.Ordinal);
        Assert.Contains("Model phi-4-mini (", output, StringComparison.Ordinal);
        Assert.Equal(2, CountOf(output, "raw valid-sentence share 0.600 (3/5)"));
        Assert.Equal(2, CountOf(output, "refusal rate             0.250 (1/4)"));
        Assert.Matches(@"cites an expected ruling \d\.\d{3} \(\d/\d question\(s\) with an expected ruling among the passages\)", output);
        Assert.Matches(@"first token p50 \d+ ms, p95 \d+ ms; total p50 \d+ ms, p95 \d+ ms", output);
        Assert.Contains("process peak working set", output, StringComparison.Ordinal);
        Assert.Contains("Not recorded; pass --record", output, StringComparison.Ordinal);
        Assert.Equal(before, Snapshot());
    }

    [Fact]
    public async Task AnswerEvalRecordsTranscriptsAndKeepsTheQuestionFilesUnchanged()
    {
        await Index();
        var questions = WriteQuestions("cirs.json",
            ("q1", "Posso deduzir as despesas de educação dos filhos?", "piv_90001"),
            ("q2", "As rendas das partes comuns do condomínio são rendimentos prediais?", "piv_90003"));
        var hash = Sha(questions);
        var record = Path.Combine(_temp.Path, "out", "answer-eval.json");

        var first = await Run("answer-eval", "--questions", questions, "--sample", "2", "--record", "--out", record);
        var second = await Run("answer-eval", "--questions", questions, "--sample", "2", "--model", "eurollm-9b", "--record", "--out", record);

        Assert.Equal((CliApp.ExitOk, CliApp.ExitOk), (first.Exit, second.Exit));
        Assert.Equal(hash, Sha(questions));
        using var document = JsonDocument.Parse(File.ReadAllText(record));
        var root = document.RootElement;
        Assert.Equal(["q1", "q2"], root.GetProperty("sample").EnumerateArray().Select(e => e.GetString()));
        var models = root.GetProperty("models").EnumerateArray().ToList();
        Assert.Equal(["eurollm-9b", "qwen3-4b"], models.Select(m => m.GetProperty("name").GetString()));
        var qwen = models[1];
        Assert.Equal(FakeAnswerGenerator.FakeModelId, qwen.GetProperty("model").GetString());
        Assert.Equal("Apache-2.0", qwen.GetProperty("licence").GetString());
        Assert.Equal(1.0, qwen.GetProperty("metrics").GetProperty("rawValidShare").GetDouble());
        var transcript = qwen.GetProperty("transcripts")[0];
        Assert.Equal("q1", transcript.GetProperty("id").GetString());
        Assert.Equal("answered", transcript.GetProperty("status").GetString());
        Assert.StartsWith("Segundo a fonte, ", transcript.GetProperty("rawOutput").GetString(), StringComparison.Ordinal);
        Assert.EndsWith("[1]", transcript.GetProperty("answer").GetString(), StringComparison.Ordinal);
        Assert.Equal(1, transcript.GetProperty("provided")[0].GetProperty("id").GetInt32());
        Assert.StartsWith("https://info.portaldasfinancas.gov.pt/", transcript.GetProperty("provided")[0].GetProperty("sourceUrl").GetString(),
            StringComparison.Ordinal);
        Assert.Contains($"Recorded in {record}", second.Out, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSampleIsSpreadEvenlyOverEachSetAndShared()
    {
        static EvalQuestionSet Set(string prefix, int count) => EvalQuestionSet.Parse(JsonSerializer.Serialize(new
        {
            questions = Enumerable.Range(1, count).Select(i => new { id = $"{prefix}{i:00}", question = $"Pergunta {prefix} {i}?", expected = new[] { "x" } }),
        }), prefix);

        var sample = AnswerCommands.Sample([("a.json", Set("q", 50)), ("b.json", Set("t", 24))], 13);

        Assert.Equal(["q01", "q08", "q15", "q22", "q29", "q36", "q43", "t01", "t05", "t09", "t13", "t17", "t21"],
            sample.Select(s => s.Question.Id));
        Assert.Equal(["q01", "q02"], AnswerCommands.Sample([("a.json", Set("q", 2))], 12).Select(s => s.Question.Id));
    }

    [Fact]
    public void PercentilesUseTheNearestRank()
    {
        var values = Enumerable.Range(1, 20).Select(i => (double)i).ToList();

        Assert.Equal(10, AnswerCommands.Percentile(values, 0.50));
        Assert.Equal(19, AnswerCommands.Percentile(values, 0.95));
        Assert.Equal(0, AnswerCommands.Percentile([], 0.95));
    }

    private Dictionary<string, string> Snapshot() => Directory.GetFiles(_temp.Path, "*", SearchOption.AllDirectories)
        .Where(f => !f.Contains(".db", StringComparison.Ordinal))
        .ToDictionary(f => f, Sha);

    private static string Sha(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));

    private static int CountOf(string text, string value) => (text.Length - text.Replace(value, "", StringComparison.Ordinal).Length) / value.Length;
}
