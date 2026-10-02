using LupaFiscal.Core.Corpus;

namespace LupaFiscal.Core.Indexing;

/// <summary>How far the index of one tax matches its extracted corpus.</summary>
public sealed record IndexStatusReport(
    string Tax,
    int Extracted,
    int Indexed,
    int Missing,
    int Outdated,
    int Stale,
    int Chunks,
    int Vectors)
{
    /// <summary>Every extracted ruling has current chunks, every chunk has a vector, nothing stale.</summary>
    public bool IsComplete => Extracted > 0 && Missing == 0 && Outdated == 0 && Stale == 0 && Chunks > 0 && Vectors == Chunks;

    public static IndexStatusReport Compute(IndexDatabase database, CorpusStore store, string modelId, int dimensions,
        ChunkingOptions options)
    {
        var corpus = IndexBuilder.LoadCorpus(store, modelId, dimensions, options);
        var states = database.RulingStates(store.Source.Code, dimensions);
        int indexed = 0, missing = 0, outdated = 0, chunks = 0, vectors = 0;
        foreach (var item in corpus)
        {
            if (!states.TryGetValue(item.Ruling.Id, out var state) || state.Chunks == 0)
            {
                missing++;
                continue;
            }
            indexed++;
            if (state.ChunkKey != item.ChunkKey) outdated++;
        }
        var wanted = corpus.Select(c => c.Ruling.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var state in states.Values)
        {
            chunks += state.Chunks;
            vectors += state.Vectors;
        }
        var stale = states.Keys.Count(id => !wanted.Contains(id));
        return new IndexStatusReport(store.Source.Code, corpus.Count, indexed, missing, outdated, stale, chunks, vectors);
    }
}
