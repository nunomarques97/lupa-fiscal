using LupaFiscal.Core.Corpus;

namespace LupaFiscal.Core.Indexing;

/// <summary>
/// How far the index matches the extracted corpus of one tax. A listed, extracted ruling counts as
/// indexed when its listing resolves to its ruling (itself or the canonical ruling with the same PDF,
/// <see cref="Merged"/>) with this tax and article, and that ruling has chunks. Chunks and vectors
/// are those of the distinct rulings the tax's listings resolve to.
/// </summary>
public sealed record IndexStatusReport(
    string Tax,
    int Extracted,
    int Indexed,
    int Missing,
    int Outdated,
    int Stale,
    int Chunks,
    int Vectors,
    int Merged = 0)
{
    /// <summary>Every extracted ruling is findable with current chunks, every chunk has a vector, nothing stale.</summary>
    public bool IsComplete => Extracted > 0 && Missing == 0 && Outdated == 0 && Stale == 0 && Chunks > 0 && Vectors == Chunks;

    /// <summary>The report of one corpus on its own (no other tax to merge with).</summary>
    public static IndexStatusReport Compute(IndexDatabase database, CorpusStore store, string modelId, int dimensions,
        ChunkingOptions options) =>
        Compute(database, [store], [store.Source.Code], modelId, dimensions, options).Single();

    /// <param name="stores">Every available corpus, so merges into an earlier tax are resolved as the index command does.</param>
    /// <param name="taxes">The taxes to report, in the order given.</param>
    public static IReadOnlyList<IndexStatusReport> Compute(IndexDatabase database, IReadOnlyList<CorpusStore> stores,
        IEnumerable<string> taxes, string modelId, int dimensions, ChunkingOptions options)
    {
        var plan = CorpusPlan.Load(stores, modelId, dimensions, options);
        var states = database.RulingStates(dimensions);
        var stored = database.Listings();
        var rulings = plan.Rulings.ToDictionary(r => r.Ruling.Id, StringComparer.Ordinal);
        var planListings = plan.Listings.ToDictionary(l => l.Id, StringComparer.Ordinal);

        var reports = new List<IndexStatusReport>();
        foreach (var tax in taxes)
        {
            int extracted = 0, indexed = 0, missing = 0, outdated = 0, merged = 0, chunks = 0, vectors = 0;
            var served = new HashSet<string>(StringComparer.Ordinal);
            foreach (var listing in plan.Listings.Where(l => l.Tax == tax))
            {
                extracted++;
                if (listing.RulingId != listing.Id) merged++;
                var ruling = rulings[listing.RulingId];
                if (!stored.TryGetValue(listing.Id, out var row) || row != listing
                    || !states.TryGetValue(ruling.Ruling.Id, out var state) || state.Chunks == 0)
                {
                    missing++;
                    continue;
                }
                indexed++;
                if (state.ChunkKey != ruling.ChunkKey) outdated++;
                if (served.Add(ruling.Ruling.Id))
                {
                    chunks += state.Chunks;
                    vectors += state.Vectors;
                }
            }
            var stale = states.Count(s => s.Value.Tax == tax && !rulings.ContainsKey(s.Key))
                + stored.Values.Count(l => l.Tax == tax && !planListings.ContainsKey(l.Id));
            reports.Add(new IndexStatusReport(tax, extracted, indexed, missing, outdated, stale, chunks, vectors, merged));
        }
        return reports;
    }
}
