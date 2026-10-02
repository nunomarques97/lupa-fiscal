namespace LupaFiscal.Core.Search;

public static class ReciprocalRankFusion
{
    /// <summary>
    /// Fuses ranked lists (best first, ranks from 1): score(id) = sum over lists of 1 / (k + rank).
    /// Ties are broken by the best single rank, then by id, so the order is deterministic.
    /// A repeated id within one list counts only at its first position.
    /// </summary>
    public static List<(long Id, double Score)> Fuse(IReadOnlyList<IReadOnlyList<long>> lists, int k)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(k);
        var scores = new Dictionary<long, (double Score, int BestRank)>();
        foreach (var list in lists)
        {
            var seen = new HashSet<long>();
            var rank = 0;
            foreach (var id in list)
            {
                if (!seen.Add(id)) continue;
                rank++;
                var contribution = 1.0 / (k + rank);
                scores[id] = scores.TryGetValue(id, out var current)
                    ? (current.Score + contribution, Math.Min(current.BestRank, rank))
                    : (contribution, rank);
            }
        }
        return scores
            .OrderByDescending(s => s.Value.Score)
            .ThenBy(s => s.Value.BestRank)
            .ThenBy(s => s.Key)
            .Select(s => (s.Key, s.Value.Score))
            .ToList();
    }
}
