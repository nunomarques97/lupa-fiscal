using System.Diagnostics;
using System.Globalization;
using LupaFiscal.Core.Embeddings;
using LupaFiscal.Core.Evaluation;
using LupaFiscal.Core.Indexing;
using LupaFiscal.Core.Search;

namespace LupaFiscal.Cli;

/// <summary>The eval and bench commands: retrieval quality and latency over the frozen question set.</summary>
internal static class EvalCommands
{
    public const string DefaultReport = "docs/eval/report.md";
    public const double DefaultMaxMs = 1000;

    /// <summary>The search settings under evaluation: the defaults the search command and API use.</summary>
    public static SearchOptions Search { get; } = new();

    public static int Eval(Arguments args, TextWriter stdout, TextWriter stderr, Func<IEmbedder>? embedderFactory,
        CancellationToken cancellationToken)
    {
        args.EnsureOnly("questions", "out", "min-recall", "record", "freeze", "label", "note", "data-dir");
        var questionsPath = args.Get("questions") ?? throw new ArgumentException(
            "Usage: eval --questions FILE [--out FILE] [--min-recall R] [--record [--label L] [--note N] | --freeze]");
        var minRecall = args.GetDouble("min-recall");
        if (minRecall is < 0 or > 1) throw new ArgumentException("--min-recall must be between 0 and 1.");
        var record = args.Has("record");
        var freeze = args.Has("freeze");
        if (record && freeze) throw new ArgumentException("--record and --freeze cannot be combined: freeze first, then measure.");
        if (!record && (args.Has("label") || args.Has("note"))) throw new ArgumentException("--label and --note name a recorded iteration; add --record.");
        var reportPath = ReportPath(args);

        var set = EvalQuestionSet.Load(questionsPath);
        var indexPath = IndexPath(args);
        string config;
        using (var database = IndexDatabase.OpenReadOnly(indexPath))
        {
            var unknown = set.UnknownRulings(database.RulingIds());
            if (unknown.Count > 0)
            {
                foreach (var (question, ruling) in unknown) stderr.WriteLine($"Question {question}: expected ruling {ruling} is not in the index.");
                stderr.WriteLine($"{unknown.Count} expected ruling(s) are not in the index {indexPath}; no report written.");
                return CliApp.ExitFailure;
            }
            config = Describe(database);
        }

        var historyPath = EvalHistory.PathFor(reportPath);
        var history = EvalHistory.Load(historyPath);
        // A frozen set is checked on every run, recorded or not, so no number is ever reported for edited questions.
        history.EnsureFrozen(set);

        if (freeze)
        {
            history.Freeze(set, RelativeToRepository(questionsPath));
            history.TargetRecallAt10 = minRecall ?? history.TargetRecallAt10;
            history.Save(historyPath);
            EvalReport.Write(reportPath, history);
            stdout.WriteLine($"Froze {questionsPath}: {set.Questions.Count} questions, {set.ExpectedCount} expected rulings, hash {set.Hash[..16]}, " +
                $"in {historyPath}. Nothing was measured.");
            return CliApp.ExitOk;
        }

        using var embedder = embedderFactory?.Invoke() ?? E5Embedder.Load(CliApp.ModelStoreFor(args));
        var searcher = HybridSearcher.Open(indexPath, embedder);
        var evaluator = new RetrievalEvaluator(searcher, Search);
        stdout.WriteLine($"Eval {questionsPath}: {set.Questions.Count} questions, {set.ExpectedCount} expected rulings, hash {set.Hash[..16]}.");
        stdout.WriteLine($"Configuration: {config}");

        var results = new List<ModeResult>();
        foreach (var mode in new[] { SearchMode.Keyword, SearchMode.Vector, SearchMode.Hybrid })
        {
            var result = evaluator.Evaluate(set, mode, cancellationToken);
            results.Add(result);
            stdout.WriteLine($"  {EvalHistory.ModeName(mode),-7}  recall@10 {Number(result.RecallAtK)}  MRR@10 {Number(result.Mrr)}  " +
                $"coverage@10 {Number(result.CoverageAtK)}  ({result.Answered}/{result.Outcomes.Count} answered)");
        }

        // Scores are written only when asked, like bench timings: a plain eval is a check and leaves the files alone.
        if (record)
        {
            var iteration = history.Record(set, RelativeToRepository(questionsPath), config, results, args.Get("label"), args.Get("note"),
                DateTimeOffset.UtcNow);
            history.TargetRecallAt10 = minRecall ?? history.TargetRecallAt10;
            history.Save(historyPath);
            EvalReport.Write(reportPath, history);
            stdout.WriteLine($"Recorded as \"{iteration.Label}\" ({history.Iterations.Count} iteration(s)); report {reportPath}");
        }
        else
        {
            stdout.WriteLine("Not recorded; pass --record to update the report and its history.");
        }

        var hybrid = results.Single(r => r.Mode == SearchMode.Hybrid);
        var failed = hybrid.Outcomes.Where(o => o.Recall == 0).Select(o => o.Question.Id).ToList();
        if (failed.Count > 0) stdout.WriteLine($"Hybrid misses: {string.Join(", ", failed)}");
        if (minRecall is { } min && hybrid.RecallAtK < min)
        {
            stderr.WriteLine($"Hybrid recall@10 {Number(hybrid.RecallAtK)} is below --min-recall {Number(min)}.");
            return CliApp.ExitFailure;
        }
        return CliApp.ExitOk;
    }

    public static int Bench(Arguments args, TextWriter stdout, TextWriter stderr, Func<IEmbedder>? embedderFactory,
        CancellationToken cancellationToken)
    {
        args.EnsureOnly("questions", "out", "max-ms", "record", "data-dir");
        var questionsPath = args.Get("questions") ?? throw new ArgumentException("Usage: bench --questions FILE [--max-ms N] [--record] [--out FILE]");
        var maxMs = args.GetDouble("max-ms") ?? DefaultMaxMs;
        if (maxMs <= 0) throw new ArgumentException("--max-ms must be greater than 0.");
        var reportPath = ReportPath(args);

        var set = EvalQuestionSet.Load(questionsPath);
        var indexPath = IndexPath(args);
        string config;
        using (var database = IndexDatabase.OpenReadOnly(indexPath))
        {
            config = Describe(database);
        }

        var load = Stopwatch.StartNew();
        using var embedder = embedderFactory?.Invoke() ?? E5Embedder.Load(CliApp.ModelStoreFor(args));
        var searcher = HybridSearcher.Open(indexPath, embedder);
        load.Stop();
        // Load-time lists are garbage once the matrix is built; collect them so the working set reflects what search keeps.
        GC.Collect();
        GC.WaitForPendingFinalizers();
        using (var process = Process.GetCurrentProcess())
        {
            stdout.WriteLine($"Loaded {searcher.RulingCount} rulings and {searcher.ChunkCount} chunk vectors: vector matrix {Mb(searcher.VectorBytes)}, " +
                $"process working set {Mb(process.WorkingSet64)}, managed heap {Mb(GC.GetTotalMemory(false))}.");
        }

        var bench = LatencyBenchmark.Run(searcher, Search, set, cancellationToken);
        bench.RecordedAt = DateTimeOffset.UtcNow;
        bench.Config = config;
        bench.LoadMs = load.Elapsed.TotalMilliseconds;
        bench.LimitMs = maxMs;

        stdout.WriteLine($"Bench {questionsPath}: {bench.Queries} hybrid queries after one warm-up ({Ms(bench.WarmupMs)}); " +
            $"model and vectors loaded in {Ms(bench.LoadMs)}.");
        stdout.WriteLine($"  p50 {Ms(bench.P50Ms)}  p95 {Ms(bench.P95Ms)}  max {Ms(bench.MaxMs)}  mean {Ms(bench.MeanMs)}  (limit {Ms(maxMs)})");

        // Timings differ on every run, so they are written only when asked: a plain bench is a check and leaves the files alone.
        if (args.Has("record"))
        {
            var historyPath = EvalHistory.PathFor(reportPath);
            var history = EvalHistory.Load(historyPath);
            history.Bench = bench;
            history.Save(historyPath);
            EvalReport.Write(reportPath, history);
            stdout.WriteLine($"Recorded in {reportPath}");
        }
        else
        {
            stdout.WriteLine("Not recorded; pass --record to update the report.");
        }
        if (bench.MaxMs >= maxMs)
        {
            stderr.WriteLine($"Slowest query took {Ms(bench.MaxMs)}, reaching the --max-ms limit of {Ms(maxMs)}.");
            return CliApp.ExitFailure;
        }
        return CliApp.ExitOk;
    }

    /// <summary>
    /// The retrieval parameters an iteration may change, and the size of the index they ran on, as recorded
    /// in the history. Scores over a grown index are a new row, so the earlier ones are kept.
    /// </summary>
    internal static string Describe(IndexDatabase database) =>
        $"model {database.GetMeta("model") ?? "(none)"}; {database.GetMeta("chunking") ?? "(no chunking recorded)"}; " +
        $"{Search.CandidatePool} candidates per list, RRF k {Search.RrfK}; index of {database.RulingIds().Count} rulings";

    private static string ReportPath(Arguments args) =>
        Path.GetFullPath(args.Get("out") ?? Path.Combine(DataDirectory.RepositoryRoot(), DefaultReport));

    private static string IndexPath(Arguments args) =>
        Path.Combine(DataDirectory.Resolve(args.Get("data-dir")), IndexCommands.IndexFileName);

    /// <summary>The path as written in the report: relative to the repository when inside it, with forward slashes.</summary>
    private static string RelativeToRepository(string path)
    {
        var full = Path.GetFullPath(path);
        var relative = Path.GetRelativePath(DataDirectory.RepositoryRoot(), full);
        return (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative) ? Path.GetFileName(full) : relative)
            .Replace('\\', '/');
    }

    private static string Number(double value) => value.ToString("0.000", CultureInfo.InvariantCulture);

    private static string Ms(double value) => value.ToString("0.0", CultureInfo.InvariantCulture) + " ms";

    private static string Mb(long bytes) => (bytes / (1024.0 * 1024.0)).ToString("0.0", CultureInfo.InvariantCulture) + " MB";
}
