using System.Diagnostics;
using System.Globalization;
using LupaFiscal.Core.Corpus;
using LupaFiscal.Core.Crawling;
using LupaFiscal.Core.Embeddings;
using LupaFiscal.Core.Indexing;
using LupaFiscal.Core.Search;

namespace LupaFiscal.Cli;

/// <summary>The index, index-status and search commands.</summary>
internal static class IndexCommands
{
    public const string IndexFileName = "lupa-fiscal.db";
    public const int MaxLimit = 50;

    public static ChunkingOptions Chunking { get; } = new();

    public static async Task<int> IndexAsync(Arguments args, TextWriter stdout, Func<HttpMessageHandler>? handlerFactory,
        Func<IEmbedder>? embedderFactory, CancellationToken cancellationToken)
    {
        args.EnsureOnly("tax", "data-dir");
        var (stores, selected) = Stores(args);
        var log = new TimestampedWriter(stdout);

        IEmbedder embedder;
        if (embedderFactory is not null)
        {
            embedder = embedderFactory();
        }
        else
        {
            var models = CliApp.ModelStoreFor(args);
            if (!Enumerable.All(models.Spec.Files, models.IsValid))
            {
                await CliApp.DownloadModelAsync(models, log, handlerFactory, cancellationToken);
            }
            embedder = E5Embedder.Load(models);
        }

        using (embedder)
        using (var database = IndexDatabase.OpenForWrite(IndexPath(args)))
        {
            log.WriteLine($"Index {IndexPath(args)}; model {embedder.ModelId}; {Chunking.Describe()}.");
            var builder = new IndexBuilder(database, embedder, Chunking, log);
            IndexBuildSummary summary;
            try
            {
                // Every available corpus takes part in merges and removals; --tax only narrows what is embedded.
                summary = builder.Build(stores, cancellationToken, args.Has("tax") ? selected : null);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                log.WriteLine("Index interrupted; finished batches are saved, run index again to resume.");
                return CliApp.ExitFailure;
            }
            log.WriteLine($"Index: {summary.Rulings} ruling(s) from {summary.Listings} listing(s) ({summary.Merged} merged); " +
                $"embedded {summary.Embedded} ({summary.ChunksWritten} chunk(s)), unchanged {summary.Unchanged}, removed {summary.Removed}.");
            var reports = IndexStatusReport.Compute(database, stores, selected, embedder.ModelId, embedder.Dimensions, Chunking);
            foreach (var report in reports) WriteReport(stdout, report);
            return reports.All(r => r.IsComplete) ? CliApp.ExitOk : CliApp.ExitFailure;
        }
    }

    public static int IndexStatus(Arguments args, TextWriter stdout, TextWriter stderr, Func<IEmbedder>? embedderFactory)
    {
        args.EnsureOnly("tax", "data-dir");
        var (stores, selected) = Stores(args);
        var path = IndexPath(args);
        if (!File.Exists(path))
        {
            stderr.WriteLine($"No index at {path}. Run: index");
            return CliApp.ExitFailure;
        }

        // The model is not loaded here: its id and size are enough to tell current chunks from outdated ones.
        string modelId;
        int dimensions;
        if (embedderFactory is null)
        {
            modelId = ModelSpec.MultilingualE5Small.Id;
            dimensions = E5Embedder.ModelDimensions;
        }
        else
        {
            using var embedder = embedderFactory();
            (modelId, dimensions) = (embedder.ModelId, embedder.Dimensions);
        }

        using var database = IndexDatabase.OpenReadOnly(path);
        stdout.WriteLine($"Index {path}");
        stdout.WriteLine($"Model: {database.GetMeta("model") ?? "(none)"}; {database.GetMeta("chunking") ?? "(no chunking recorded)"}");
        var reports = IndexStatusReport.Compute(database, stores, selected, modelId, dimensions, Chunking);
        foreach (var report in reports) WriteReport(stdout, report);

        if (reports.All(r => r.IsComplete))
        {
            stdout.WriteLine("Status: complete (every extracted ruling has chunks and every chunk has a vector).");
            return CliApp.ExitOk;
        }
        stdout.WriteLine("Status: incomplete. Run: index");
        return CliApp.ExitFailure;
    }

    public static int Search(IReadOnlyList<string> positional, Arguments args, TextWriter stdout, Func<IEmbedder>? embedderFactory)
    {
        args.EnsureOnly("query", "tax", "article", "year", "limit", "mode", "data-dir");
        var query = args.Get("query") ?? string.Join(' ', positional);
        if (string.IsNullOrWhiteSpace(query) || (positional.Count > 0 && args.Has("query")))
        {
            throw new ArgumentException("Usage: search \"question\" [--tax T [--article A]] [--year Y] [--limit N] [--mode hybrid|keyword|vector]");
        }
        var limit = args.GetInt("limit") ?? 10;
        if (limit is < 1 or > MaxLimit) throw new ArgumentException($"--limit must be between 1 and {MaxLimit}.");
        var mode = args.Get("mode") switch
        {
            null or "hybrid" => SearchMode.Hybrid,
            "keyword" => SearchMode.Keyword,
            "vector" => SearchMode.Vector,
            var other => throw new ArgumentException($"Unknown --mode '{other}' (hybrid, keyword or vector)."),
        };
        var filters = new SearchFilters(Blank(args.Get("tax")), Blank(args.Get("article")), args.GetInt("year"));
        if (filters.Article is not null && filters.Tax is null)
        {
            throw new ArgumentException("--article needs --tax: article numbers are only meaningful within one tax code.");
        }

        var load = Stopwatch.StartNew();
        using var embedder = embedderFactory?.Invoke() ?? E5Embedder.Load(CliApp.ModelStoreFor(args));
        var searcher = HybridSearcher.Open(IndexPath(args), embedder);
        load.Stop();

        var result = searcher.Search(query, filters, new SearchOptions { Limit = limit, Mode = mode });
        stdout.WriteLine($"Query: {query}");
        stdout.WriteLine($"Mode: {mode.ToString().ToLowerInvariant()}; {searcher.RulingCount} rulings, {searcher.ChunkCount} chunks; " +
            $"model and vectors loaded in {load.Elapsed.TotalMilliseconds:0} ms.");
        stdout.WriteLine();
        var rank = 0;
        foreach (var hit in result.Hits)
        {
            rank++;
            var date = hit.Date?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "sem data";
            stdout.WriteLine($"{rank}. [{hit.Score:0.0000}] {hit.Tax} art. {hit.Article} | processo {hit.ProcessNumber} | {date} | {hit.Section}");
            stdout.WriteLine($"   {hit.Subject}");
            stdout.WriteLine($"   {hit.SourceUrl}");
            stdout.WriteLine($"   {Excerpt(hit)}");
            stdout.WriteLine();
        }
        if (result.Hits.Count == 0) stdout.WriteLine("No results.");
        stdout.WriteLine($"Search took {result.ElapsedMs:0} ms ({result.Hits.Count} result(s)).");
        return CliApp.ExitOk;
    }

    private static void WriteReport(TextWriter writer, IndexStatusReport report)
    {
        writer.WriteLine($"{report.Tax} extracted rulings: {report.Extracted}");
        writer.WriteLine($"  indexed rulings:        {report.Indexed}");
        writer.WriteLine($"  merged (same PDF as an earlier tax): {report.Merged}");
        writer.WriteLine($"  missing (no chunks):    {report.Missing}");
        writer.WriteLine($"  outdated chunks:        {report.Outdated}");
        writer.WriteLine($"  stale (not in corpus):  {report.Stale}");
        writer.WriteLine($"  chunks:                 {report.Chunks}");
        writer.WriteLine($"  chunks with vectors:    {report.Vectors}");
    }

    /// <summary>The passage on one line, highlighted terms in [brackets], cut after about 600 characters.</summary>
    private static string Excerpt(SearchHit hit)
    {
        const int Max = 600;
        var builder = new System.Text.StringBuilder();
        var position = 0;
        foreach (var highlight in hit.Highlights)
        {
            if (highlight.Start >= Max) break;
            builder.Append(hit.Passage, position, highlight.Start - position).Append('[')
                .Append(hit.Passage, highlight.Start, highlight.Length).Append(']');
            position = highlight.Start + highlight.Length;
        }
        var end = Math.Max(position, Math.Min(hit.Passage.Length, Max));
        builder.Append(hit.Passage, position, end - position);
        if (end < hit.Passage.Length) builder.Append(" ...");
        return builder.ToString().Replace('\n', ' ');
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string IndexPath(Arguments args) => Path.Combine(DataDirectory.Resolve(args.Get("data-dir")), IndexFileName);

    /// <summary>
    /// Every supported tax that has a crawled corpus (in source order), and the ones the command is
    /// about: the --tax one, or all of them.
    /// </summary>
    private static (List<CorpusStore> Stores, List<string> Selected) Stores(Arguments args)
    {
        var root = CliApp.CorpusRoot(args);
        var stores = TaxSource.All.Select(source => new CorpusStore(root, source)).Where(s => File.Exists(s.ManifestPath)).ToList();
        if (args.Has("tax"))
        {
            var source = CliApp.RequireTax(args);
            var store = stores.FirstOrDefault(s => s.Source == source)
                ?? throw new InvalidOperationException($"No corpus for {source.Code} under {root}. Run: crawl --tax {source.Code}");
            return (stores, [store.Source.Code]);
        }
        return stores.Count > 0
            ? (stores, stores.Select(s => s.Source.Code).ToList())
            : throw new InvalidOperationException($"No corpus under {root}. Run: crawl --all");
    }
}
