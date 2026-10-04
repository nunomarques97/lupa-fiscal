using System.Diagnostics;
using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using LupaFiscal.Core.Answering;
using LupaFiscal.Core.Embeddings;
using LupaFiscal.Core.Evaluation;
using LupaFiscal.Core.Search;

namespace LupaFiscal.Cli;

/// <summary>The answer and answer-eval commands: strict-citation answers from a local model.</summary>
internal static class AnswerCommands
{
    public const string DefaultRecord = "docs/research/answer-eval.json";
    public const int DefaultSample = 12;

    /// <summary>The model used when --model is not given.</summary>
    public static AnswerModel DefaultModel => AnswerModel.Default;

    public static AnswerOptions Options { get; } = new();

    public static int Answer(IReadOnlyList<string> positional, Arguments args, TextWriter stdout,
        Func<IEmbedder>? embedderFactory, Func<AnswerModel, IAnswerGenerator>? generatorFactory, CancellationToken cancellationToken)
    {
        args.EnsureOnly("query", "tax", "article", "year", "model", "data-dir");
        var query = args.Get("query") ?? string.Join(' ', positional);
        if (string.IsNullOrWhiteSpace(query) || (positional.Count > 0 && args.Has("query")))
        {
            throw new ArgumentException("Usage: answer \"question\" [--tax T [--article A]] [--year Y] [--model NAME]");
        }
        if (query.Length > EvalQuestionSet.MaxQuestionLength)
        {
            throw new ArgumentException($"The question is longer than {EvalQuestionSet.MaxQuestionLength} characters.");
        }
        var filters = IndexCommands.Filters(args);
        var model = args.Has("model") ? CliApp.RequireAnswerModel(args.Get("model")) : DefaultModel;

        var load = Stopwatch.StartNew();
        using var generator = LoadGenerator(args, model, generatorFactory);
        using var embedder = embedderFactory?.Invoke() ?? E5Embedder.Load(CliApp.ModelStoreFor(args));
        var searcher = HybridSearcher.Open(IndexCommands.IndexPath(args), embedder);
        load.Stop();

        var answer = new Answerer(searcher, generator, Options).Answer(query, filters, cancellationToken);
        stdout.WriteLine($"Query: {query}");
        stdout.WriteLine($"Model: {model.Name} ({answer.ModelId}); loaded with the embedding model and vectors in {Ms(load.Elapsed.TotalMilliseconds)}.");
        stdout.WriteLine($"Passages in the prompt: {answer.Provided.Count} ({answer.PromptTokens} prompt tokens).");
        stdout.WriteLine();
        stdout.WriteLine("Resposta:");
        if (answer.Status == AnswerStatus.Refused)
        {
            stdout.WriteLine(CitationEnforcer.RefusalText);
        }
        else
        {
            foreach (var sentence in answer.Sentences)
            {
                stdout.WriteLine($"{sentence.Text} {string.Concat(sentence.Citations.Select(id => $"[{id}]"))}");
            }
            stdout.WriteLine();
            stdout.WriteLine("Fontes:");
            foreach (var citation in answer.Citations)
            {
                var hit = citation.Hit;
                var date = hit.Date?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "sem data";
                stdout.WriteLine($"[{citation.Id}] processo {hit.ProcessNumber} | {hit.Tax} art. {hit.Article} | {date} | {hit.SourceUrl}");
            }
        }
        stdout.WriteLine();
        stdout.WriteLine($"Timings: search {Ms(answer.SearchMs)}, first token {Ms(answer.FirstTokenMs)}, generation {Ms(answer.GenerationMs)}, " +
            $"total {Ms(answer.TookMs)}; {answer.GeneratedTokens} token(s) generated, stop: {StopName(answer.Stop)}.");
        stdout.WriteLine($"Model output: {answer.ValidRawSentences} of {answer.RawSentences} sentence(s) with valid citations before filtering.");
        return CliApp.ExitOk;
    }

    public static int AnswerEval(Arguments args, TextWriter stdout, TextWriter stderr, Func<IEmbedder>? embedderFactory,
        Func<AnswerModel, IAnswerGenerator>? generatorFactory, CancellationToken cancellationToken)
    {
        args.EnsureOnly("questions", "sample", "model", "record", "out", "data-dir");
        var files = (args.Get("questions") ?? throw new ArgumentException(
                "Usage: answer-eval --questions FILE[,FILE] [--sample N] [--model NAME[,NAME]] [--record [--out FILE]]"))
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (files.Length == 0) throw new ArgumentException("--questions needs at least one file.");
        var sample = args.GetInt("sample") ?? DefaultSample;
        if (sample < 1) throw new ArgumentException("--sample must be at least 1.");
        var models = (args.Get("model") ?? DefaultModel.Name)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(CliApp.RequireAnswerModel).Distinct().ToList();
        if (models.Count == 0) throw new ArgumentException("--model needs at least one name.");
        var record = args.Has("record");
        if (!record && args.Has("out")) throw new ArgumentException("--out names the recorded file; add --record.");
        var outPath = Path.GetFullPath(args.Get("out") ?? Path.Combine(DataDirectory.RepositoryRoot(), DefaultRecord));

        // The question sets are only read: nothing in this command writes them.
        var sets = files.Select(file => (File: file, Set: EvalQuestionSet.Load(file))).ToList();
        var questions = Sample(sets.Select(s => (s.File, s.Set)).ToList(), sample);
        stdout.WriteLine($"Answer eval: {questions.Count} question(s) sampled from {string.Join(", ", files)} " +
            $"({string.Join(", ", questions.Select(q => q.Question.Id))}).");

        using var embedder = embedderFactory?.Invoke() ?? E5Embedder.Load(CliApp.ModelStoreFor(args));
        var searcher = HybridSearcher.Open(IndexCommands.IndexPath(args), embedder);

        var summaries = new List<JsonObject>();
        foreach (var model in models)
        {
            var load = Stopwatch.StartNew();
            using var generator = LoadGenerator(args, model, generatorFactory);
            load.Stop();
            var answerer = new Answerer(searcher, generator, Options);
            stdout.WriteLine();
            stdout.WriteLine($"Model {model.Name} ({generator.ModelId}), loaded in {Ms(load.Elapsed.TotalMilliseconds)}:");

            var rows = new List<(SampledQuestion Question, AnswerResult Answer, bool ExpectedProvided, bool ExpectedCited)>();
            foreach (var question in questions)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var answer = answerer.Answer(question.Question.Question, new SearchFilters(), cancellationToken);
                var expected = question.Question.Expected.ToHashSet(StringComparer.Ordinal);
                var provided = answer.Provided.Any(c => expected.Contains(c.Hit.RulingId));
                var cited = answer.Citations.Any(c => expected.Contains(c.Hit.RulingId));
                rows.Add((question, answer, provided, cited));
                stdout.WriteLine($"  {question.Question.Id,-4} {(answer.Status == AnswerStatus.Answered ? "answered" : "refused "),-8}  " +
                    $"valid {answer.ValidRawSentences}/{answer.RawSentences}  expected {(provided ? cited ? "cited" : "not cited" : "not provided")}  " +
                    $"first token {Ms(answer.FirstTokenMs)}  total {Ms(answer.TookMs)}");
            }

            using var process = Process.GetCurrentProcess();
            var metrics = Metrics(rows.Select(r => (r.Answer, r.ExpectedProvided, r.ExpectedCited)).ToList());
            var peak = process.PeakWorkingSet64;
            stdout.WriteLine($"  raw valid-sentence share {Number(metrics.RawValidShare)} ({metrics.ValidSentences}/{metrics.RawSentences})");
            stdout.WriteLine($"  cites an expected ruling {Number(metrics.ExpectedCitedShare)} ({metrics.ExpectedCited}/{metrics.ExpectedProvided} " +
                "question(s) with an expected ruling among the passages)");
            stdout.WriteLine($"  refusal rate             {Number(metrics.RefusalRate)} ({metrics.Refusals}/{rows.Count})");
            stdout.WriteLine($"  first token p50 {Ms(metrics.FirstTokenP50)}, p95 {Ms(metrics.FirstTokenP95)}; " +
                $"total p50 {Ms(metrics.TotalP50)}, p95 {Ms(metrics.TotalP95)}");
            stdout.WriteLine($"  process peak working set {Mb(peak)} (embedding model, vectors and every model loaded so far in this run)");

            summaries.Add(Record(model, generator.ModelId, load.Elapsed.TotalMilliseconds, peak, metrics, rows));
        }

        if (record)
        {
            Save(outPath, files, questions, summaries);
            stdout.WriteLine($"Recorded in {outPath}");
        }
        else
        {
            stdout.WriteLine();
            stdout.WriteLine("Not recorded; pass --record to write the transcripts and numbers.");
        }
        return CliApp.ExitOk;
    }

    internal sealed record SampledQuestion(string File, EvalQuestion Question);

    internal sealed record EvalMetrics(
        int RawSentences, int ValidSentences, double RawValidShare,
        int ExpectedProvided, int ExpectedCited, double ExpectedCitedShare,
        int Refusals, double RefusalRate,
        double FirstTokenP50, double FirstTokenP95, double TotalP50, double TotalP95);

    /// <summary>
    /// A deterministic spread sample: the count is shared evenly between the sets (the first sets take
    /// the remainder, and a set too small for its share passes the rest on), and each set contributes
    /// questions at even steps from its first question.
    /// </summary>
    internal static List<SampledQuestion> Sample(IReadOnlyList<(string File, EvalQuestionSet Set)> sets, int count)
    {
        var shares = sets.Select((_, s) => count / sets.Count + (s < count % sets.Count ? 1 : 0)).ToArray();
        var excess = 0;
        for (var s = 0; s < sets.Count; s++)
        {
            excess += Math.Max(0, shares[s] - sets[s].Set.Questions.Count);
            shares[s] = Math.Min(shares[s], sets[s].Set.Questions.Count);
        }
        for (var s = 0; s < sets.Count && excess > 0; s++)
        {
            var add = Math.Min(excess, sets[s].Set.Questions.Count - shares[s]);
            shares[s] += add;
            excess -= add;
        }

        var sampled = new List<SampledQuestion>();
        for (var s = 0; s < sets.Count; s++)
        {
            var (file, set) = sets[s];
            for (var k = 0; k < shares[s]; k++) sampled.Add(new SampledQuestion(file, set.Questions[k * set.Questions.Count / shares[s]]));
        }
        return sampled;
    }

    internal static EvalMetrics Metrics(IReadOnlyList<(AnswerResult Answer, bool ExpectedProvided, bool ExpectedCited)> rows)
    {
        var raw = rows.Sum(r => r.Answer.RawSentences);
        var valid = rows.Sum(r => r.Answer.ValidRawSentences);
        var provided = rows.Count(r => r.ExpectedProvided);
        var cited = rows.Count(r => r.ExpectedProvided && r.ExpectedCited);
        var refusals = rows.Count(r => r.Answer.Status == AnswerStatus.Refused);
        var first = rows.Select(r => r.Answer.FirstTokenMs).ToList();
        var total = rows.Select(r => r.Answer.TookMs).ToList();
        return new EvalMetrics(raw, valid, raw == 0 ? 0 : (double)valid / raw, provided, cited, provided == 0 ? 0 : (double)cited / provided,
            refusals, rows.Count == 0 ? 0 : (double)refusals / rows.Count,
            Percentile(first, 0.50), Percentile(first, 0.95), Percentile(total, 0.50), Percentile(total, 0.95));
    }

    /// <summary>Nearest-rank percentile, the same definition as the latency benchmark.</summary>
    internal static double Percentile(IReadOnlyList<double> values, double p)
    {
        if (values.Count == 0) return 0;
        var sorted = values.Order().ToList();
        var rank = (int)Math.Ceiling(p * sorted.Count);
        return sorted[Math.Clamp(rank - 1, 0, sorted.Count - 1)];
    }

    private static IAnswerGenerator LoadGenerator(Arguments args, AnswerModel model, Func<AnswerModel, IAnswerGenerator>? generatorFactory)
    {
        if (generatorFactory is not null) return generatorFactory(model);
        var store = CliApp.AnswerModelStoreFor(args, model);
        if (!store.AllFilesPresent)
        {
            throw new ModelIntegrityException($"Answer model {model.Name} is not in {store.DirectoryPath}. Run: model download --model {model.Name}");
        }
        return OnnxGenAiGenerator.Load(store, model);
    }

    private static JsonObject Record(AnswerModel model, string modelId, double loadMs, long peak, EvalMetrics metrics,
        IReadOnlyList<(SampledQuestion Question, AnswerResult Answer, bool ExpectedProvided, bool ExpectedCited)> rows) => new()
    {
        ["name"] = model.Name,
        ["model"] = modelId,
        ["licence"] = model.Licence,
        ["sizeOnDiskBytes"] = model.Spec.Files.Sum(f => f.Size),
        ["recordedAt"] = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
        ["machine"] = $"{Environment.ProcessorCount} logical processors, {Environment.OSVersion.VersionString}",
        ["loadMs"] = Math.Round(loadMs),
        ["peakWorkingSetBytes"] = peak,
        ["settings"] = $"{Options.Prompt.MaxPassages} passages, {Options.Prompt.MaxPassageTokens} tokens each at most, prompt budget " +
            $"{Options.Prompt.PromptTokenBudget} tokens, {Options.Prompt.MaxNewTokens} new tokens, time limit {Options.TimeLimit.TotalSeconds:0} s, greedy",
        ["metrics"] = new JsonObject
        {
            ["rawSentences"] = metrics.RawSentences,
            ["validRawSentences"] = metrics.ValidSentences,
            ["rawValidShare"] = Math.Round(metrics.RawValidShare, 3),
            ["questionsWithExpectedProvided"] = metrics.ExpectedProvided,
            ["questionsCitingExpected"] = metrics.ExpectedCited,
            ["expectedCitedShare"] = Math.Round(metrics.ExpectedCitedShare, 3),
            ["refusals"] = metrics.Refusals,
            ["refusalRate"] = Math.Round(metrics.RefusalRate, 3),
            ["firstTokenP50Ms"] = Math.Round(metrics.FirstTokenP50),
            ["firstTokenP95Ms"] = Math.Round(metrics.FirstTokenP95),
            ["totalP50Ms"] = Math.Round(metrics.TotalP50),
            ["totalP95Ms"] = Math.Round(metrics.TotalP95),
        },
        ["transcripts"] = new JsonArray(rows.Select(r => (JsonNode)new JsonObject
        {
            ["id"] = r.Question.Question.Id,
            ["set"] = r.Question.File.Replace('\\', '/'),
            ["question"] = r.Question.Question.Question,
            ["expected"] = new JsonArray(r.Question.Question.Expected.Select(e => (JsonNode)e).ToArray()),
            ["expectedProvided"] = r.ExpectedProvided,
            ["expectedCited"] = r.ExpectedCited,
            ["status"] = r.Answer.Status == AnswerStatus.Answered ? "answered" : "refused",
            ["answer"] = r.Answer.Text,
            ["rawOutput"] = r.Answer.RawOutput,
            ["rawSentences"] = r.Answer.RawSentences,
            ["validRawSentences"] = r.Answer.ValidRawSentences,
            ["provided"] = new JsonArray(r.Answer.Provided.Select(c => (JsonNode)new JsonObject
            {
                ["id"] = c.Id,
                ["rulingId"] = c.Hit.RulingId,
                ["processNumber"] = c.Hit.ProcessNumber,
                ["tax"] = c.Hit.Tax,
                ["article"] = c.Hit.Article,
                ["sourceUrl"] = c.Hit.SourceUrl,
                ["truncated"] = c.Truncated,
            }).ToArray()),
            ["promptTokens"] = r.Answer.PromptTokens,
            ["generatedTokens"] = r.Answer.GeneratedTokens,
            ["stop"] = StopName(r.Answer.Stop),
            ["searchMs"] = Math.Round(r.Answer.SearchMs),
            ["firstTokenMs"] = Math.Round(r.Answer.FirstTokenMs),
            ["totalMs"] = Math.Round(r.Answer.TookMs),
        }).ToArray()),
    };

    /// <summary>Writes the record, replacing the entries of the models measured now and keeping the others.</summary>
    private static void Save(string path, IReadOnlyList<string> files, IReadOnlyList<SampledQuestion> questions, IReadOnlyList<JsonObject> summaries)
    {
        var root = File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path))?.AsObject() ?? [] : [];
        var sampleIds = new JsonArray(questions.Select(q => (JsonNode)q.Question.Id).ToArray());
        if (root["sample"]?.ToJsonString() != sampleIds.ToJsonString()) root["models"] = new JsonArray();
        root["questions"] = new JsonArray(files.Select(f => (JsonNode)f.Replace('\\', '/')).ToArray());
        root["sample"] = sampleIds;
        var models = root["models"] as JsonArray ?? [];
        foreach (var summary in summaries)
        {
            var name = summary["name"]!.GetValue<string>();
            var existing = models.FirstOrDefault(m => m?["name"]?.GetValue<string>() == name);
            if (existing is not null) models.Remove(existing);
            models.Add(summary);
        }
        root["models"] = new JsonArray(models.OrderBy(m => m!["name"]!.GetValue<string>(), StringComparer.Ordinal)
            .Select(m => m!.DeepClone()).ToArray());
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) + "\n");
    }

    private static string StopName(GenerationStop? stop) => stop switch
    {
        GenerationStop.EndOfText => "end of text",
        GenerationStop.TokenLimit => "token limit",
        GenerationStop.TimeLimit => "time limit",
        _ => "no passages, model not called",
    };

    private static string Number(double value) => value.ToString("0.000", CultureInfo.InvariantCulture);

    private static string Ms(double value) => value.ToString("0", CultureInfo.InvariantCulture) + " ms";

    private static string Mb(long bytes) => (bytes / (1024.0 * 1024.0)).ToString("0", CultureInfo.InvariantCulture) + " MB";
}
