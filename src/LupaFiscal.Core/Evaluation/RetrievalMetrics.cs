namespace LupaFiscal.Core.Evaluation;

/// <summary>
/// Ruling-level retrieval metrics. Every function takes the ruling ids in the order the searcher
/// returned them (one per passage, so a ruling may repeat) and first reduces them to distinct
/// rulings, keeping each at its first position: several chunks of one ruling count once and do
/// not push other rulings down. Rulings with tied scores keep the searcher's order (its
/// deterministic tie-break), which is the order a user sees, so ranks are list positions.
/// </summary>
public static class RetrievalMetrics
{
    /// <summary>Distinct ruling ids in first-seen order.</summary>
    public static IReadOnlyList<string> DistinctRulings(IEnumerable<string> ranked)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        return ranked.Where(seen.Add).ToList();
    }

    /// <summary>1-based rank of the first expected ruling among the first <paramref name="k"/> distinct rulings, or null.</summary>
    public static int? FirstRelevantRank(IEnumerable<string> ranked, IReadOnlyCollection<string> expected, int k)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(k);
        var rank = 0;
        foreach (var id in DistinctRulings(ranked))
        {
            if (++rank > k) break;
            if (expected.Contains(id, StringComparer.Ordinal)) return rank;
        }
        return null;
    }

    /// <summary>
    /// Recall@k of one question: 1 when at least one expected ruling is among the first k
    /// distinct rulings, else 0. The expected rulings of a question are alternative answers, so
    /// this is the usual question-answering retrieval convention (a correct source was retrieved).
    /// </summary>
    public static double RecallAtK(IEnumerable<string> ranked, IReadOnlyCollection<string> expected, int k) =>
        FirstRelevantRank(ranked, expected, k) is null ? 0 : 1;

    /// <summary>Reciprocal rank of the first expected ruling within the first k distinct rulings (0 when none).</summary>
    public static double ReciprocalRank(IEnumerable<string> ranked, IReadOnlyCollection<string> expected, int k) =>
        FirstRelevantRank(ranked, expected, k) is { } rank ? 1.0 / rank : 0;

    /// <summary>
    /// Share of the expected rulings found among the first k distinct rulings, out of at most k
    /// (so a question with more than k expected rulings can still reach 1). Reported alongside
    /// recall@k to show how many of the alternative answers were found.
    /// </summary>
    public static double CoverageAtK(IEnumerable<string> ranked, IReadOnlyCollection<string> expected, int k)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(k);
        if (expected.Count == 0) return 0;
        var found = DistinctRulings(ranked).Take(k).Count(id => expected.Contains(id, StringComparer.Ordinal));
        return (double)found / Math.Min(expected.Count, k);
    }

    /// <summary>Nearest-rank percentile (p in (0, 100]) of the values: the smallest value with at least p % of values at or below it.</summary>
    public static double Percentile(IEnumerable<double> values, double p)
    {
        if (p is <= 0 or > 100) throw new ArgumentOutOfRangeException(nameof(p), "Percentile must be in (0, 100].");
        var sorted = values.Order().ToList();
        if (sorted.Count == 0) throw new ArgumentException("No values.", nameof(values));
        // The epsilon keeps exact products (95 % of 20 = 19) from rounding up to the next rank.
        var rank = (int)Math.Ceiling(p * sorted.Count / 100 - 1e-9);
        return sorted[Math.Clamp(rank, 1, sorted.Count) - 1];
    }
}
