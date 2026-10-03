using System.Text.Json;
using System.Text.Json.Serialization;
using LupaFiscal.Core.Search;

namespace LupaFiscal.Core.Evaluation;

/// <summary>Scores of one mode in one iteration.</summary>
public sealed class ModeSummary
{
    public double RecallAt10 { get; set; }

    public double MrrAt10 { get; set; }

    public double CoverageAt10 { get; set; }

    public int Answered { get; set; }

    public int Questions { get; set; }

    public static ModeSummary From(ModeResult result) => new()
    {
        RecallAt10 = result.RecallAtK,
        MrrAt10 = result.Mrr,
        CoverageAt10 = result.CoverageAtK,
        Answered = result.Answered,
        Questions = result.Outcomes.Count,
    };

    public bool SameScores(ModeSummary other) =>
        RecallAt10 == other.RecallAt10 && MrrAt10 == other.MrrAt10 && CoverageAt10 == other.CoverageAt10 &&
        Answered == other.Answered && Questions == other.Questions;
}

/// <summary>
/// One evaluated retrieval configuration. Running eval again with the same configuration replaces it;
/// RecordedAt only moves when its scores, label or note change, so rerunning an unchanged eval leaves the files as they are.
/// </summary>
public sealed class EvalIteration
{
    public string Label { get; set; } = "";

    public string Config { get; set; } = "";

    public string? Note { get; set; }

    public DateTimeOffset RecordedAt { get; set; }

    public Dictionary<string, ModeSummary> Modes { get; set; } = [];
}

/// <summary>Per-question ranks of the latest eval run, for the failed-question and per-question tables.</summary>
public sealed class QuestionRecord
{
    public string Id { get; set; } = "";

    /// <summary>Tax code of the question, absent in a single-tax set.</summary>
    public string? Tax { get; set; }

    public string Article { get; set; } = "";

    public string Question { get; set; } = "";

    public List<string> Expected { get; set; } = [];

    /// <summary>Rank of the first expected ruling per mode within the search depth, null when absent.</summary>
    public Dictionary<string, int?> FirstExpectedRank { get; set; } = [];

    /// <summary>The first hybrid results, to show what was returned instead on a miss.</summary>
    public List<string> HybridTop { get; set; } = [];
}

public sealed class BenchRecord
{
    public DateTimeOffset RecordedAt { get; set; }

    public string Config { get; set; } = "";

    public string QuestionsHash { get; set; } = "";

    public int Queries { get; set; }

    public double LoadMs { get; set; }

    public double WarmupMs { get; set; }

    public double P50Ms { get; set; }

    public double P95Ms { get; set; }

    public double MaxMs { get; set; }

    public double MeanMs { get; set; }

    public double LimitMs { get; set; }
}

/// <summary>
/// The eval record kept next to the report (docs/eval/history.json): the frozen question-set hash,
/// every iteration's scores, the latest per-question ranks and the latest latency benchmark.
/// The report is rendered from it, so eval and bench each update their part without losing the other.
/// </summary>
public sealed class EvalHistory
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string? QuestionsPath { get; set; }

    public string? QuestionsHash { get; set; }

    public int QuestionCount { get; set; }

    public int ExpectedCount { get; set; }

    /// <summary>The --min-recall of the latest eval run, when one was given.</summary>
    public double? TargetRecallAt10 { get; set; }

    public List<EvalIteration> Iterations { get; set; } = [];

    public string? LatestConfig { get; set; }

    public List<QuestionRecord> Latest { get; set; } = [];

    public BenchRecord? Bench { get; set; }

    public static string PathFor(string reportPath) =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(reportPath))!, "history.json");

    public static EvalHistory Load(string path)
    {
        if (!File.Exists(path)) return new EvalHistory();
        try
        {
            return JsonSerializer.Deserialize<EvalHistory>(File.ReadAllBytes(path), JsonOptions) ?? new EvalHistory();
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"Eval history {path} is not valid ({ex.Message}). Fix or remove it.");
        }
    }

    public void Save(string path) => WriteAtomic(path, JsonSerializer.SerializeToUtf8Bytes(this, JsonOptions));

    /// <summary>
    /// Throws when a question set was frozen here (by <see cref="Freeze"/> or a recorded iteration) and
    /// <paramref name="set"/> differs from it, so no measurement is taken against edited questions or answers.
    /// </summary>
    public void EnsureFrozen(EvalQuestionSet set)
    {
        if (QuestionsHash is { } frozen && frozen != set.Hash)
        {
            throw new InvalidDataException(
                $"The question set changed after it was frozen with {Iterations.Count} recorded iteration(s) (hash {set.Hash[..12]}, recorded {frozen[..12]}). " +
                "Questions and expected rulings are frozen before tuning; restore the frozen file.");
        }
    }

    /// <summary>Pins the question set by its hash before any measurement. Freezing the same set again changes nothing.</summary>
    public void Freeze(EvalQuestionSet set, string questionsPath)
    {
        EnsureFrozen(set);
        QuestionsPath = questionsPath;
        QuestionsHash = set.Hash;
        QuestionCount = set.Questions.Count;
        ExpectedCount = set.ExpectedCount;
    }

    /// <summary>
    /// Records an eval run. The question set is frozen once it has a hash here: a different hash
    /// throws, so tuning can never be measured against edited questions or answers.
    /// </summary>
    public EvalIteration Record(EvalQuestionSet set, string questionsPath, string config, IReadOnlyList<ModeResult> results,
        string? label, string? note, DateTimeOffset now)
    {
        Freeze(set, questionsPath);

        var iteration = Iterations.FirstOrDefault(i => i.Config == config);
        if (iteration is null)
        {
            iteration = new EvalIteration { Label = label ?? (Iterations.Count == 0 ? "baseline" : $"iteration {Iterations.Count}") };
            Iterations.Add(iteration);
        }
        var modes = results.ToDictionary(r => ModeName(r.Mode), ModeSummary.From);
        var newNote = note ?? iteration.Note;
        var unchanged = iteration.Config == config && iteration.Note == newNote && SameScores(iteration.Modes, modes) &&
            (label is null || iteration.Label == label);
        if (label is not null) iteration.Label = label;
        iteration.Config = config;
        iteration.Note = newNote;
        if (!unchanged) iteration.RecordedAt = now;
        iteration.Modes = modes;

        LatestConfig = config;
        Latest = set.Questions.Select((q, i) => new QuestionRecord
        {
            Id = q.Id,
            Tax = q.Tax.Length > 0 ? q.Tax : null,
            Article = q.Article,
            Question = q.Question,
            Expected = [.. q.Expected],
            FirstExpectedRank = results.ToDictionary(r => ModeName(r.Mode), r => r.Outcomes[i].FirstExpectedRank),
            HybridTop = results.FirstOrDefault(r => r.Mode == SearchMode.Hybrid)?.Outcomes[i].Rulings.Take(3).ToList() ?? [],
        }).ToList();
        return iteration;
    }

    public static string ModeName(SearchMode mode) => mode.ToString().ToLowerInvariant();

    private static bool SameScores(Dictionary<string, ModeSummary> recorded, Dictionary<string, ModeSummary> current) =>
        recorded.Count == current.Count &&
        recorded.All(pair => current.TryGetValue(pair.Key, out var summary) && pair.Value.SameScores(summary));

    /// <summary>Replaces the file atomically, and leaves it untouched (contents and timestamp) when the bytes are the same.</summary>
    internal static void WriteAtomic(string path, byte[] content)
    {
        if (File.Exists(path) && File.ReadAllBytes(path).AsSpan().SequenceEqual(content)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + ".partial";
        File.WriteAllBytes(temporary, content);
        File.Move(temporary, path, overwrite: true);
    }
}
