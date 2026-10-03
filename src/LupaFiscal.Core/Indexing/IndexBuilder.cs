using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using LupaFiscal.Core.Corpus;
using LupaFiscal.Core.Embeddings;

namespace LupaFiscal.Core.Indexing;

public sealed record IndexBuildSummary(int Rulings, int Embedded, int Unchanged, int Removed, int ChunksWritten,
    int Listings = 0, int Merged = 0);

/// <summary>A ruling of the corpus that should be in the index, with the key of its expected chunks and every listing it stands for.</summary>
public sealed record CorpusRuling(IndexedRuling Ruling, string ChunkKey, IReadOnlyList<IndexedListing> Listings);

/// <summary>
/// The rulings the index should hold, built from the listed, extracted rulings of the given corpora.
/// Identity is the PDF content: a ruling whose PDF SHA-256 equals one already listed by an earlier
/// tax (in <see cref="Crawling.TaxSource.All"/> order, CIRS first) is not a ruling of its own but one
/// more listing of that canonical ruling, which keeps its display tax and id. Entries of one tax are
/// kept as published, even with identical PDFs, so v0.1 CIRS ids never change; the canonical of a
/// tax's identical PDFs is its lowest id. Rulings without a recorded hash are never merged.
/// </summary>
public sealed class CorpusPlan
{
    private CorpusPlan(List<CorpusRuling> rulings, List<string> taxes)
    {
        Rulings = rulings;
        Taxes = taxes;
        RulingOfListing = rulings.SelectMany(r => r.Listings).ToDictionary(l => l.Id, l => l.RulingId, StringComparer.Ordinal);
    }

    /// <summary>Canonical rulings, tax by tax in source order, each tax in manifest order.</summary>
    public IReadOnlyList<CorpusRuling> Rulings { get; }

    /// <summary>Codes of the corpora the plan was built from, in source order.</summary>
    public IReadOnlyList<string> Taxes { get; }

    /// <summary>Listing id to the id of the ruling it is stored as.</summary>
    public IReadOnlyDictionary<string, string> RulingOfListing { get; }

    public IEnumerable<IndexedListing> Listings => Rulings.SelectMany(r => r.Listings);

    public int MergedCount => Listings.Count(l => l.Id != l.RulingId);

    public static CorpusPlan Load(IEnumerable<CorpusStore> stores, string modelId, int dimensions, ChunkingOptions options)
    {
        var ordered = stores
            .DistinctBy(s => s.Source.Code)
            .OrderBy(s => IndexOf(s.Source))
            .ToList();
        var settings = $"{options.Describe()}|{modelId}|{dimensions}";
        var rulings = new List<CorpusRuling>();
        var byId = new Dictionary<string, (IndexedRuling Ruling, string Key, List<IndexedListing> Listings)>(StringComparer.Ordinal);
        // PDF hash to the canonical ruling id, filled after each tax so entries of one tax never merge.
        var canonicalOfHash = new Dictionary<string, string>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var store in ordered)
        {
            var manifest = store.LoadManifest()
                ?? throw new InvalidOperationException($"No corpus for {store.Source.Code} at {store.TaxDirectory}. Run: crawl --tax {store.Source.Code}");
            var records = manifest.Rulings.Where(r => r.Listed && r.State == RulingState.Extracted).ToList();
            var order = new List<string>();
            foreach (var record in records)
            {
                if (!seen.Add(record.Id))
                {
                    throw new InvalidDataException($"Ruling id {record.Id} appears twice in the corpus.");
                }
                var listing = new IndexedListing(record.Id, record.Id, record.Tax, record.Article);
                if (record.PdfSha256 is { Length: > 0 } hash && canonicalOfHash.TryGetValue(hash, out var canonical))
                {
                    byId[canonical].Listings.Add(listing with { RulingId = canonical });
                    continue;
                }

                var text = File.ReadAllText(store.TextPath(record.Id), Encoding.UTF8);
                var body = RulingSections.NormalizeBody(text);
                var ruling = new IndexedRuling(record.Id, record.Tax, record.Diploma, record.Article, record.Paragraph,
                    record.PublishedOn, record.DecisionDate, record.ProcessNumber, record.Subject, record.SourceUrl, body,
                    string.IsNullOrEmpty(record.PdfSha256) ? null : record.PdfSha256);
                var key = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(settings + "\n" + body)));
                byId[record.Id] = (ruling, key, [listing]);
                order.Add(record.Id);
            }

            foreach (var group in records
                         .Where(r => r.PdfSha256 is { Length: > 0 } && byId.ContainsKey(r.Id))
                         .GroupBy(r => r.PdfSha256!, StringComparer.Ordinal))
            {
                canonicalOfHash.TryAdd(group.Key, group.Select(r => r.Id).Min(StringComparer.Ordinal)!);
            }
            rulings.AddRange(order.Select(id => new CorpusRuling(byId[id].Ruling, byId[id].Key, byId[id].Listings)));
        }
        return new CorpusPlan(rulings, ordered.Select(s => s.Source.Code).ToList());
    }

    private static int IndexOf(Crawling.TaxSource source)
    {
        var index = Crawling.TaxSource.All.ToList().IndexOf(source);
        return index < 0 ? int.MaxValue : index;
    }
}

/// <summary>
/// Brings the index in line with the extracted corpora. Rulings and listings are reconciled first,
/// in one transaction: rulings and listings no longer in the corpus are removed, metadata is
/// refreshed and every listing points at its canonical ruling. Then a ruling is chunked and embedded
/// only when it is new, its text or the chunking/model settings changed (its chunk key differs), or
/// one of its chunks lacks a vector, so running the command again never duplicates chunks. Each
/// batch is committed in one transaction, so an interrupted run resumes where it stopped.
/// </summary>
public sealed class IndexBuilder(IndexDatabase database, IEmbedder embedder, ChunkingOptions options, TextWriter log)
{
    private const int RulingsPerBatch = 32;

    private readonly Chunker _chunker = new(embedder, options);

    public IndexBuildSummary Build(CorpusStore store, CancellationToken cancellationToken) => Build([store], cancellationToken);

    /// <param name="stores">Every available corpus: the plan, merges and removals cover all of them.</param>
    /// <param name="embedTaxes">When given, only rulings listed by one of these taxes are chunked and embedded.</param>
    public IndexBuildSummary Build(IReadOnlyList<CorpusStore> stores, CancellationToken cancellationToken,
        IReadOnlyCollection<string>? embedTaxes = null)
    {
        var stopwatch = Stopwatch.StartNew();
        var plan = CorpusPlan.Load(stores, embedder.ModelId, embedder.Dimensions, options);
        var states = database.RulingStates(embedder.Dimensions);
        var listings = database.Listings();

        var removed = 0;
        using (var transaction = database.BeginTransaction())
        {
            var wanted = plan.Rulings.Select(c => c.Ruling.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var id in states.Keys.Where(id => !wanted.Contains(id)))
            {
                database.DeleteRuling(id, transaction);
                removed++;
            }
            foreach (var id in listings.Keys.Where(id => !plan.RulingOfListing.ContainsKey(id)))
            {
                database.DeleteListing(id, transaction);
            }
            // Metadata (subject, dates...) is refreshed for every ruling; chunks are untouched here.
            foreach (var item in plan.Rulings) database.UpsertRuling(item.Ruling, transaction);
            foreach (var listing in plan.Listings)
            {
                if (!listings.TryGetValue(listing.Id, out var current) || current != listing) database.UpsertListing(listing, transaction);
            }
            transaction.Commit();
        }
        if (removed > 0) log.WriteLine($"Removed {removed} ruling(s) that are no longer extracted in the corpus.");

        var stale = plan.Rulings.Where(c => !IsCurrent(c, states)).ToList();
        var todo = stale
            .Where(c => embedTaxes is null || c.Listings.Any(l => embedTaxes.Contains(l.Tax, StringComparer.OrdinalIgnoreCase)))
            .ToList();
        var listed = plan.Listings.Count();
        log.WriteLine($"{string.Join(", ", plan.Taxes)}: {listed} extracted listing(s), {plan.MergedCount} merged into the same PDF " +
            $"of an earlier tax, {plan.Rulings.Count} distinct ruling(s); {plan.Rulings.Count - stale.Count} already indexed, " +
            $"{todo.Count} to chunk and embed" + (todo.Count < stale.Count ? $", {stale.Count - todo.Count} left for another tax." : "."));

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
        return new IndexBuildSummary(plan.Rulings.Count, todo.Count, plan.Rulings.Count - stale.Count, removed, chunksWritten,
            listed, plan.MergedCount);
    }

    internal static bool IsCurrent(CorpusRuling item, IReadOnlyDictionary<string, RulingIndexState> states) =>
        states.TryGetValue(item.Ruling.Id, out var state)
        && state.ChunkKey == item.ChunkKey
        && state.Chunks > 0
        && state.Vectors == state.Chunks;
}
