using System.Diagnostics;
using LupaFiscal.Core.Search;

namespace LupaFiscal.Core.Evaluation;

/// <summary>Outcome of one question in one mode: the distinct rulings returned (up to the search depth) and its scores at k.</summary>
public sealed record QuestionOutcome(
    EvalQuestion Question,
    IReadOnlyList<string> Rulings,
    int? FirstExpectedRank,
    double Recall,
    double ReciprocalRank,
    double Coverage);

/// <summary>Averages over the question set for one search mode.</summary>
public sealed record ModeResult(SearchMode Mode, IReadOnlyList<QuestionOutcome> Outcomes)
{
    public double RecallAtK => Outcomes.Count == 0 ? 0 : Outcomes.Average(o => o.Recall);

    public double Mrr => Outcomes.Count == 0 ? 0 : Outcomes.Average(o => o.ReciprocalRank);

    public double CoverageAtK => Outcomes.Count == 0 ? 0 : Outcomes.Average(o => o.Coverage);

    public int Answered => Outcomes.Count(o => o.Recall > 0);
}

/// <summary>
/// Runs every question through the searcher in one mode, with no filters and one passage per
/// ruling. Scores are computed at <see cref="K"/>; the search goes to <see cref="Depth"/> so a
/// miss still shows where the first expected ruling landed.
/// </summary>
public sealed class RetrievalEvaluator(HybridSearcher searcher, SearchOptions options)
{
    public const int K = 10;
    public const int Depth = 50;

    public ModeResult Evaluate(EvalQuestionSet set, SearchMode mode, CancellationToken cancellationToken = default)
    {
        var searchOptions = options with { Mode = mode, Limit = Depth, OnePassagePerRuling = true };
        var outcomes = new List<QuestionOutcome>(set.Questions.Count);
        foreach (var question in set.Questions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = searcher.Search(question.Question, new SearchFilters(), searchOptions);
            var rulings = RetrievalMetrics.DistinctRulings(result.Hits.Select(h => h.RulingId));
            outcomes.Add(new QuestionOutcome(
                question,
                rulings,
                RetrievalMetrics.FirstRelevantRank(rulings, question.Expected.ToList(), Depth),
                RetrievalMetrics.RecallAtK(rulings, question.Expected.ToList(), K),
                RetrievalMetrics.ReciprocalRank(rulings, question.Expected.ToList(), K),
                RetrievalMetrics.CoverageAtK(rulings, question.Expected.ToList(), K)));
        }
        return new ModeResult(mode, outcomes);
    }
}

/// <summary>Latency of hybrid search (query embedding included) over the question set, after one warm-up query.</summary>
public static class LatencyBenchmark
{
    public static BenchRecord Run(HybridSearcher searcher, SearchOptions options, EvalQuestionSet set, CancellationToken cancellationToken = default)
    {
        var searchOptions = options with { Mode = SearchMode.Hybrid, Limit = RetrievalEvaluator.K };
        var warmup = Stopwatch.StartNew();
        searcher.Search(set.Questions[0].Question, new SearchFilters(), searchOptions);
        warmup.Stop();

        var timings = new List<double>(set.Questions.Count);
        foreach (var question in set.Questions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var stopwatch = Stopwatch.StartNew();
            searcher.Search(question.Question, new SearchFilters(), searchOptions);
            stopwatch.Stop();
            timings.Add(stopwatch.Elapsed.TotalMilliseconds);
        }

        return new BenchRecord
        {
            Queries = timings.Count,
            WarmupMs = warmup.Elapsed.TotalMilliseconds,
            P50Ms = RetrievalMetrics.Percentile(timings, 50),
            P95Ms = RetrievalMetrics.Percentile(timings, 95),
            MaxMs = timings.Max(),
            MeanMs = timings.Average(),
            QuestionsHash = set.Hash,
        };
    }
}
