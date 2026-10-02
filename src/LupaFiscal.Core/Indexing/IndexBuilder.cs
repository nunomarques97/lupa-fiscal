using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using LupaFiscal.Core.Corpus;
using LupaFiscal.Core.Embeddings;

namespace LupaFiscal.Core.Indexing;

public sealed record IndexBuildSummary(int Rulings, int Embedded, int Unchanged, int Removed, int ChunksWritten);

/// <summary>A ruling of the corpus that should be in the index, with the key of its expected chunks.</summary>
public sealed record CorpusRuling(IndexedRuling Ruling, string ChunkKey);

/// <summary>
/// Brings the index in line with the extracted corpus. A ruling is chunked and embedded only when
/// it is new, its text or the chunking/model settings changed (its chunk key differs), or one of
/// its chunks lacks a vector. Unchanged rulings only get their metadata refreshed, so running the
/// command again never duplicates chunks. Rulings no longer extracted are removed. Each batch is
/// committed in one transaction, so an interrupted run resumes where it stopped.
/// </summary>
public sealed class IndexBuilder(IndexDatabase database, IEmbedder embedder, ChunkingOptions options, TextWriter log)
{
    private const int RulingsPerBatch = 32;

    private readonly Chunker _chunker = new(embedder, options);

    public IndexBuildSummary Build(CorpusStore store, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var corpus = LoadCorpus(store, embedder.ModelId, embedder.Dimensions, options);
        var states = database.RulingStates(store.Source.Code, embedder.Dimensions);

        var removed = 0;
        using (var transaction = database.BeginTransaction())
        {
            var wanted = corpus.Select(c => c.Ruling.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var id in states.Keys.Where(id => !wanted.Contains(id)))
            {
                database.DeleteRuling(id, transaction);
                removed++;
            }
            // Metadata (subject, dates...) is refreshed for every ruling; chunks are untouched here.
            foreach (var item in corpus) database.UpsertRuling(item.Ruling, transaction);
            transaction.Commit();
        }
        if (removed > 0) log.WriteLine($"Removed {removed} ruling(s) that are no longer extracted in the corpus.");

        var todo = corpus.Where(c => !IsCurrent(c, states)).ToList();
        log.WriteLine($"{store.Source.Code}: {corpus.Count} extracted ruling(s), {corpus.Count - todo.Count} already indexed, {todo.Count} to chunk and embed.");

        var chunksWritten = 0;
        for (var offset = 0; offset < todo.Count; offset += RulingsPerBatch)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var batch = todo.Skip(offset).Take(RulingsPerBatch).ToList();
            var chunked = batch.Select(item => (Item: item, Chunks: _chunker.Split(item.Ruling.Body))).ToList();
            var texts = chunked.SelectMany(c => c.Chunks.Select(chunk => chunk.Text)).ToList();
            var vectors = embedder.EmbedPassages(texts, cancellationToken);

            using var transaction = database.BeginTransaction();
            var next = 0;
            foreach (var (item, chunks) in chunked)
            {
                var rows = chunks.Select(chunk => new ChunkRow(chunk, vectors[next++])).ToList();
                database.ReplaceChunks(item.Ruling.Id, item.ChunkKey, rows, transaction);
                chunksWritten += rows.Count;
            }
            transaction.Commit();

            var done = Math.Min(offset + RulingsPerBatch, todo.Count);
            log.WriteLine($"Embedded {done}/{todo.Count} ruling(s), {chunksWritten} chunk(s), {stopwatch.Elapsed.TotalSeconds:0} s.");
        }

        database.SetMeta("model", embedder.ModelId);
        database.SetMeta("dimensions", embedder.Dimensions.ToString(CultureInfo.InvariantCulture));
        database.SetMeta("chunking", options.Describe());
        if (todo.Count > 0 || removed > 0) database.Optimize();
        return new IndexBuildSummary(corpus.Count, todo.Count, corpus.Count - todo.Count, removed, chunksWritten);
    }

    private static bool IsCurrent(CorpusRuling item, Dictionary<string, (string? ChunkKey, int Chunks, int Vectors)> states) =>
        states.TryGetValue(item.Ruling.Id, out var state)
        && state.ChunkKey == item.ChunkKey
        && state.Chunks > 0
        && state.Vectors == state.Chunks;

    /// <summary>The listed, extracted rulings of the corpus with their normalised body and chunk key.</summary>
    public static List<CorpusRuling> LoadCorpus(CorpusStore store, string modelId, int dimensions, ChunkingOptions options)
    {
        var manifest = store.LoadManifest()
            ?? throw new InvalidOperationException($"No corpus for {store.Source.Code} at {store.TaxDirectory}. Run: crawl --tax {store.Source.Code}");
        var settings = $"{options.Describe()}|{modelId}|{dimensions}";
        var result = new List<CorpusRuling>();
        foreach (var record in manifest.Rulings.Where(r => r.Listed && r.State == RulingState.Extracted))
        {
            var text = File.ReadAllText(store.TextPath(record.Id), Encoding.UTF8);
            var body = RulingSections.NormalizeBody(text);
            var ruling = new IndexedRuling(record.Id, record.Tax, record.Diploma, record.Article, record.Paragraph,
                record.PublishedOn, record.DecisionDate, record.ProcessNumber, record.Subject, record.SourceUrl, body);
            var key = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(settings + "\n" + body)));
            result.Add(new CorpusRuling(ruling, key));
        }
        return result;
    }
}
