using System.Text.Json;
using LupaFiscal.Cli;
using LupaFiscal.Core.Evaluation;
using LupaFiscal.Tests.Support;

namespace LupaFiscal.Tests;

public sealed class EvalCliTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public EvalCliTests()
    {
        // Twelve fillers share every word of the insurance question, so the pension ruling (which
        // shares none) ranks after them in both lists: that question is a miss at 10.
        var fillers = Enumerable.Range(1, 12).Select(i => new TestCorpus.Ruling($"piv_91{i:000}", "2", new DateOnly(2020, 1, i),
            $"Seguros de vida {i}", $"Encargos com seguros de vida e imposto do selo sobre contratos de seguro número {i}."));
        TestCorpus.Write(_temp.Path, [.. TestCorpus.All, .. fillers]);
    }

    public void Dispose() => _temp.Dispose();

    private string ReportPath => Path.Combine(_temp.Path, "eval", "report.md");

    private string HistoryPath => Path.Combine(_temp.Path, "eval", "history.json");

    private async Task<(int Exit, string Out, string Err)> Run(params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exit = await CliApp.RunAsync([.. args, "--data-dir", _temp.Path], stdout, stderr, CancellationToken.None,
            () => throw new InvalidOperationException("no network in tests"), () => new FakeEmbedder());
        return (exit, stdout.ToString(), stderr.ToString());
    }

    private string Questions(params (string Id, string Question, string[] Expected)[] questions)
    {
        var path = Path.Combine(_temp.Path, $"questions-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            questions = questions.Select(q => new { id = q.Id, article = "1", question = q.Question, expected = q.Expected }),
        }));
        return path;
    }

    private string HitAndMiss() => Questions(
        ("q1", "Posso deduzir as despesas de educação e as propinas dos filhos?", ["piv_90001"]),
        ("q2", "Seguros de vida e imposto do selo nos contratos?", ["piv_90004"]));

    [Fact]
    public async Task EvalWritesTheReportAndFailsBelowTheMinimumRecall()
    {
        await Run("index");
        var questions = HitAndMiss();

        var passing = await Run("eval", "--questions", questions, "--out", ReportPath, "--min-recall", "0.5");
        var failing = await Run("eval", "--questions", questions, "--out", ReportPath, "--min-recall", "0.6");

        Assert.Equal(CliApp.ExitOk, passing.Exit);
        Assert.Contains("hybrid   recall@10 0.500  MRR@10 0.500", passing.Out, StringComparison.Ordinal);
        Assert.Contains("Hybrid misses: q2", passing.Out, StringComparison.Ordinal);
        Assert.Equal(CliApp.ExitFailure, failing.Exit);
        Assert.Contains("below --min-recall 0.600", failing.Err, StringComparison.Ordinal);

        var report = File.ReadAllText(ReportPath);
        Assert.Contains("| keyword | 0.500 | 0.500 |", report, StringComparison.Ordinal);
        Assert.Contains("| vector | 0.500 | 0.500 |", report, StringComparison.Ordinal);
        Assert.Contains("| hybrid | 0.500 | 0.500 |", report, StringComparison.Ordinal);
        Assert.Contains("is below the target of 0.600", report, StringComparison.Ordinal);
        Assert.Contains("| q2 | 1 | Seguros de vida", report, StringComparison.Ordinal);
        Assert.Contains("## Iterations", report, StringComparison.Ordinal);
        // The same configuration twice is one iteration, updated in place.
        Assert.Single(EvalHistory.Load(HistoryPath).Iterations);
    }

    [Fact]
    public async Task EvalRejectsInvalidQuestionsUnknownRulingsAndAnEditedFrozenSet()
    {
        await Run("index");
        var invalid = Path.Combine(_temp.Path, "invalid.json");
        File.WriteAllText(invalid, """{"questions": [{"id": "q1", "question": "Sem respostas?", "expected": []}]}""");
        var unknown = Questions(("q1", "Propinas dos filhos?", ["piv_90001", "piv_99999"]));

        var badFile = await Run("eval", "--questions", invalid, "--out", ReportPath);
        var missingFile = await Run("eval", "--questions", Path.Combine(_temp.Path, "absent.json"), "--out", ReportPath);
        var badRuling = await Run("eval", "--questions", unknown, "--out", ReportPath);

        Assert.Equal(CliApp.ExitFailure, badFile.Exit);
        Assert.Contains("lists no expected ruling", badFile.Err, StringComparison.Ordinal);
        Assert.Equal(CliApp.ExitFailure, missingFile.Exit);
        Assert.Equal(CliApp.ExitFailure, badRuling.Exit);
        Assert.Contains("Question q1: expected ruling piv_99999 is not in the index.", badRuling.Err, StringComparison.Ordinal);
        Assert.False(File.Exists(ReportPath));

        var frozen = HitAndMiss();
        Assert.Equal(CliApp.ExitOk, (await Run("eval", "--questions", frozen, "--out", ReportPath)).Exit);
        File.WriteAllText(frozen, File.ReadAllText(frozen).Replace("piv_90004", "piv_90003", StringComparison.Ordinal));
        var edited = await Run("eval", "--questions", frozen, "--out", ReportPath);

        Assert.Equal(CliApp.ExitFailure, edited.Exit);
        Assert.Contains("frozen", edited.Err, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("eval")]
    [InlineData("eval", "--questions", "q.json", "--min-recall", "1.5")]
    [InlineData("eval", "--questions", "q.json", "--max-ms", "10")]
    [InlineData("bench")]
    [InlineData("bench", "--questions", "q.json", "--max-ms", "0")]
    [InlineData("bench", "--questions", "q.json", "--max-ms", "fast")]
    public async Task RejectsInvalidArguments(params string[] args)
    {
        var (exit, _, _) = await Run(args);
        Assert.Equal(CliApp.ExitUsage, exit);
    }

    [Fact]
    public async Task BenchRecordsLatencyBesideTheEvalAndFailsAtTheLimit()
    {
        await Run("index");
        var questions = HitAndMiss();
        await Run("eval", "--questions", questions, "--out", ReportPath);

        var bench = await Run("bench", "--questions", questions, "--out", ReportPath, "--max-ms", "60000", "--record");
        var tooSlow = await Run("bench", "--questions", questions, "--out", ReportPath, "--max-ms", "0.000001", "--record");

        Assert.Equal(CliApp.ExitOk, bench.Exit);
        Assert.Contains("Recorded in", bench.Out, StringComparison.Ordinal);
        Assert.Matches(@"2 hybrid queries after one warm-up", bench.Out);
        Assert.Matches(@"p50 [\d.]+ ms  p95 [\d.]+ ms  max [\d.]+ ms", bench.Out);
        Assert.Equal(CliApp.ExitFailure, tooSlow.Exit);
        Assert.Contains("reaching the --max-ms limit", tooSlow.Err, StringComparison.Ordinal);

        var history = EvalHistory.Load(HistoryPath);
        Assert.Single(history.Iterations);
        Assert.Equal(2, history.Bench!.Queries);
        Assert.True(history.Bench.MaxMs >= history.Bench.P95Ms && history.Bench.P95Ms >= history.Bench.P50Ms);
        var report = File.ReadAllText(ReportPath);
        Assert.Contains("## Latency", report, StringComparison.Ordinal);
        Assert.Contains("| p50 | p95 | max |", report, StringComparison.Ordinal);
        Assert.Contains("## Failed questions (hybrid)", report, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RerunningEvalWithUnchangedScoresAndBenchWithoutRecordLeaveTheFilesUntouched()
    {
        await Run("index");
        var questions = HitAndMiss();
        await Run("eval", "--questions", questions, "--out", ReportPath, "--min-recall", "0.5");
        var history = File.ReadAllBytes(HistoryPath);
        var report = File.ReadAllBytes(ReportPath);
        var historyTime = File.GetLastWriteTimeUtc(HistoryPath);
        var reportTime = File.GetLastWriteTimeUtc(ReportPath);
        await Task.Delay(50);

        var eval = await Run("eval", "--questions", questions, "--out", ReportPath, "--min-recall", "0.5");
        var bench = await Run("bench", "--questions", questions, "--out", ReportPath, "--max-ms", "60000");

        Assert.Equal(CliApp.ExitOk, eval.Exit);
        Assert.Equal(CliApp.ExitOk, bench.Exit);
        Assert.Contains("Not recorded", bench.Out, StringComparison.Ordinal);
        Assert.Equal(history, File.ReadAllBytes(HistoryPath));
        Assert.Equal(report, File.ReadAllBytes(ReportPath));
        Assert.Equal(historyTime, File.GetLastWriteTimeUtc(HistoryPath));
        Assert.Equal(reportTime, File.GetLastWriteTimeUtc(ReportPath));
        Assert.Null(EvalHistory.Load(HistoryPath).Bench);
    }

    [Fact]
    public async Task BenchWithoutRecordWritesNothing()
    {
        await Run("index");

        var bench = await Run("bench", "--questions", HitAndMiss(), "--out", ReportPath, "--max-ms", "60000");

        Assert.Equal(CliApp.ExitOk, bench.Exit);
        Assert.False(File.Exists(HistoryPath));
        Assert.False(File.Exists(ReportPath));
    }
}
