using System.Globalization;
using System.Text;
using LupaFiscal.Core.Corpus;
using LupaFiscal.Core.Crawling;
using LupaFiscal.Core.Embeddings;
using LupaFiscal.Core.Extraction;

namespace LupaFiscal.Cli;

/// <summary>Command-line entry point: parses arguments and runs one command.</summary>
internal static class CliApp
{
    public const int ExitOk = 0;
    public const int ExitFailure = 1;
    public const int ExitUsage = 2;

    private const string Usage = """
        Usage: lupa-fiscal <command> [options]

        Commands:
          crawl --tax CIRS           Download the listed rulings (polite, resumable) and extract their text.
              --interval-seconds N   Gap between requests, at least 1 (default 1).
              --max-retries N        Retries on 429/5xx/network errors, 0 to 10 (default 4).
              --retry-failed         Retry rulings that failed in an earlier run.
              --use-cached-listing   Do not request the listing again; use the cached response.
              --max-downloads N      Stop after N PDF downloads.
          extract --tax CIRS         Extract the text of every cached PDF again (offline, no requests).
          corpus-status --tax CIRS   Print corpus counts; exits 0 only when no listed ruling is pending.
              --list-failed          Also list failed rulings with their reason.
          model download             Download the pinned embedding model into data/models (SHA-256 verified).
          index [--tax CIRS]         Chunk and embed every extracted ruling into data/lupa-fiscal.db (idempotent;
                                     downloads the model first if needed).
          index-status [--tax CIRS]  Print index counts; exits 0 only when every extracted ruling has chunks
                                     and every chunk has a vector.
          search "question"          Print the best passages with ruling metadata, source URL and elapsed ms.
              --tax T --article A --year Y   Filters (applied to keyword and vector results).
              --limit N              Results, 1 to 50 (default 10).
              --mode M               hybrid (default), keyword or vector.
          eval --questions FILE      Recall@10 and MRR@10 at ruling level for keyword, vector and hybrid search;
                                     writes the report and its history (docs/eval/report.md and history.json).
              --out FILE             Report path (default docs/eval/report.md in the repository).
              --min-recall R         Exit 1 when hybrid recall@10 is below R (0 to 1).
              --label L --note N     Name and describe the iteration being recorded.
          bench --questions FILE     Hybrid search latency (query embedding included) after one warm-up query;
                                     prints p50, p95 and max.
              --max-ms N             Exit 1 when the slowest query takes N ms or more (default 1000).
              --record               Also record the timings in the report and its history.
              --out FILE             Report path (default docs/eval/report.md in the repository).

        Common options:
          --data-dir PATH            Data directory (default: <repository>/data, or LUPAFISCAL_DATA_DIR).
        """;

    public static async Task<int> RunAsync(string[] args, TextWriter stdout, TextWriter stderr,
        CancellationToken cancellationToken, Func<HttpMessageHandler>? handlerFactory = null,
        Func<IEmbedder>? embedderFactory = null)
    {
        if (args.Length == 0 || args[0] is "help" or "--help" or "-h")
        {
            stdout.WriteLine(Usage);
            return args.Length == 0 ? ExitUsage : ExitOk;
        }

        // "model download" and "search <question>" take positional words before their options.
        var positional = args.Skip(1).TakeWhile(a => !a.StartsWith("--", StringComparison.Ordinal)).ToList();
        Arguments parsed;
        try
        {
            if (positional.Count > 0 && args[0] is not ("model" or "search"))
            {
                throw new ArgumentException($"Unexpected argument '{positional[0]}'.");
            }
            parsed = Arguments.Parse(args.Skip(1 + positional.Count));
        }
        catch (ArgumentException ex)
        {
            stderr.WriteLine(ex.Message);
            return ExitUsage;
        }

        try
        {
            return args[0] switch
            {
                "crawl" => await CrawlAsync(parsed, stdout, stderr, handlerFactory, cancellationToken),
                "extract" => Extract(parsed, stdout, cancellationToken),
                "corpus-status" => CorpusStatus(parsed, stdout, stderr),
                "model" => await ModelAsync(positional, parsed, stdout, handlerFactory, cancellationToken),
                "index" => await IndexCommands.IndexAsync(parsed, stdout, handlerFactory, embedderFactory, cancellationToken),
                "index-status" => IndexCommands.IndexStatus(parsed, stdout, stderr, embedderFactory),
                "search" => IndexCommands.Search(positional, parsed, stdout, embedderFactory),
                "eval" => EvalCommands.Eval(parsed, stdout, stderr, embedderFactory, cancellationToken),
                "bench" => EvalCommands.Bench(parsed, stdout, stderr, embedderFactory, cancellationToken),
                _ => UnknownCommand(args[0], stderr),
            };
        }
        catch (ArgumentException ex)
        {
            stderr.WriteLine(ex.Message);
            return ExitUsage;
        }
        catch (ModelIntegrityException ex)
        {
            stderr.WriteLine(ex.Message);
            return ExitFailure;
        }
        catch (Exception ex) when (ex is InvalidDataException or FileNotFoundException or InvalidOperationException)
        {
            stderr.WriteLine(ex.Message);
            return ExitFailure;
        }
        catch (HttpRequestException ex)
        {
            stderr.WriteLine($"Model download failed: network error ({ex.HttpRequestError}).");
            return ExitFailure;
        }
    }

    private static async Task<int> ModelAsync(IReadOnlyList<string> positional, Arguments args, TextWriter stdout,
        Func<HttpMessageHandler>? handlerFactory, CancellationToken cancellationToken)
    {
        if (positional is not ["download"])
        {
            throw new ArgumentException("Usage: model download [--data-dir PATH]");
        }
        args.EnsureOnly("data-dir");
        var store = ModelStoreFor(args);
        var log = new TimestampedWriter(stdout);
        await DownloadModelAsync(store, log, handlerFactory, cancellationToken);
        return ExitOk;
    }

    internal static async Task DownloadModelAsync(ModelStore store, TextWriter log,
        Func<HttpMessageHandler>? handlerFactory, CancellationToken cancellationToken)
    {
        log.WriteLine($"Model {store.Spec.Id} into {store.DirectoryPath}");
        using var downloader = new ModelDownloader(handlerFactory?.Invoke() ?? ModelDownloader.CreateDefaultHandler(), log);
        var summary = await downloader.EnsureAsync(store, cancellationToken);
        log.WriteLine($"Model ready: {summary.Downloaded} file(s) downloaded, {summary.AlreadyValid} already verified.");
    }

    internal static ModelStore ModelStoreFor(Arguments args) =>
        new(Path.Combine(DataDirectory.Resolve(args.Get("data-dir")), "models"), ModelSpec.MultilingualE5Small);

    private static int UnknownCommand(string command, TextWriter stderr)
    {
        stderr.WriteLine($"Unknown command '{command}'. Run 'help' for the list of commands.");
        return ExitUsage;
    }

    private static async Task<int> CrawlAsync(Arguments args, TextWriter stdout, TextWriter stderr,
        Func<HttpMessageHandler>? handlerFactory, CancellationToken cancellationToken)
    {
        var source = RequireTax(args);
        args.EnsureOnly("tax", "data-dir", "interval-seconds", "max-retries", "retry-failed", "use-cached-listing", "max-downloads");

        CrawlerOptions options;
        try
        {
            options = new CrawlerOptions
            {
                RequestInterval = TimeSpan.FromSeconds(args.GetDouble("interval-seconds") ?? 1),
                MaxRetries = args.GetInt("max-retries") ?? 4,
            };
        }
        catch (ArgumentOutOfRangeException ex)
        {
            throw new ArgumentException(ex.Message.Split(Environment.NewLine)[0], ex);
        }

        var maxDownloads = args.GetInt("max-downloads");
        if (maxDownloads is < 0) throw new ArgumentException("--max-downloads cannot be negative.");

        var store = new CorpusStore(CorpusRoot(args), source);
        Directory.CreateDirectory(store.TaxDirectory);
        await using var logFile = new StreamWriter(Path.Combine(store.TaxDirectory, "crawl.log"), append: true, Encoding.UTF8);
        var log = new TimestampedWriter(stdout, logFile);

        log.WriteLine($"Crawl {source.Code} into {store.TaxDirectory}; interval {options.RequestInterval.TotalSeconds:0.###} s, max retries {options.MaxRetries}.");
        using var client = new PoliteHttpClient(handlerFactory?.Invoke() ?? PoliteHttpClient.CreateDefaultHandler(),
            options, SystemCrawlClock.Instance, log);
        var crawler = new CorpusCrawler(client, store, new PdfTextExtractor(), options, log);
        var run = new CrawlRunOptions
        {
            RetryFailed = args.Has("retry-failed"),
            UseCachedListing = args.Has("use-cached-listing"),
            MaxDownloads = maxDownloads,
        };

        CrawlSummary summary;
        try
        {
            summary = await crawler.RunAsync(run, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            log.WriteLine($"Crawl interrupted after {client.RequestCount} request(s); progress is saved, run crawl again to resume.");
            return ExitFailure;
        }

        log.WriteLine($"Crawl finished: {summary.Requests} request(s), {summary.Downloads} PDF download(s).");
        WriteCounts(log, source, summary.Counts);
        if (summary.Aborted)
        {
            stderr.WriteLine($"Crawl stopped: {summary.AbortReason}.");
            return ExitFailure;
        }
        return summary.Counts.IsComplete ? ExitOk : ExitFailure;
    }

    private static int Extract(Arguments args, TextWriter stdout, CancellationToken cancellationToken)
    {
        var source = RequireTax(args);
        args.EnsureOnly("tax", "data-dir");
        var store = new CorpusStore(CorpusRoot(args), source);
        if (store.LoadManifest() is null)
        {
            throw new ArgumentException($"No corpus for {source.Code} at {store.TaxDirectory}. Run: crawl --tax {source.Code}");
        }

        var log = new TimestampedWriter(stdout);
        var summary = new RulingTextExtraction(store, new PdfTextExtractor(), log).ReextractAll(cancellationToken);
        log.WriteLine($"Extracted {summary.Processed} cached PDF(s) again.");
        WriteCounts(log, source, summary.Counts);
        return summary.Counts.IsComplete ? ExitOk : ExitFailure;
    }

    private static int CorpusStatus(Arguments args, TextWriter stdout, TextWriter stderr)
    {
        var source = RequireTax(args);
        args.EnsureOnly("tax", "data-dir", "list-failed");
        var store = new CorpusStore(CorpusRoot(args), source);
        var manifest = store.LoadManifest();
        if (manifest is null)
        {
            stderr.WriteLine($"No corpus for {source.Code} at {store.TaxDirectory}. Run: crawl --tax {source.Code}");
            return ExitFailure;
        }

        var counts = CorpusCounts.From(manifest);
        stdout.WriteLine($"Corpus {source.Code} ({store.TaxDirectory})");
        if (manifest.ListingFetchedAt is { } fetched)
        {
            stdout.WriteLine($"Listing fetched: {fetched.ToString("yyyy-MM-dd HH:mm:ss'Z'", CultureInfo.InvariantCulture)}");
        }
        WriteCounts(stdout, source, counts);
        if (counts.ScannedSkipped > 0)
        {
            stdout.WriteLine($"Scanned-skipped report: {store.ScannedReportPath}");
        }
        if (args.Has("list-failed"))
        {
            foreach (var ruling in manifest.Rulings.Where(r => r.Listed && r.State == RulingState.Failed))
            {
                stdout.WriteLine($"  failed {ruling.Id}: {ruling.Reason}");
            }
        }

        if (counts.IsComplete)
        {
            stdout.WriteLine("Status: complete (no listed ruling is pending).");
            return ExitOk;
        }
        stdout.WriteLine($"Status: incomplete ({counts.Pending + counts.Downloaded} listed ruling(s) still pending). Run: crawl --tax {source.Code}");
        return ExitFailure;
    }

    private static void WriteCounts(TextWriter writer, TaxSource source, CorpusCounts counts)
    {
        writer.WriteLine($"{source.Code} listed: {counts.Listed}");
        writer.WriteLine($"  pending:         {counts.Pending}");
        writer.WriteLine($"  downloaded:      {counts.PdfsCached} (awaiting extraction: {counts.Downloaded})");
        writer.WriteLine($"  extracted:       {counts.Extracted}");
        writer.WriteLine($"  scanned-skipped: {counts.ScannedSkipped}");
        writer.WriteLine($"  failed:          {counts.Failed}");
        if (counts.Delisted > 0)
        {
            writer.WriteLine($"  no longer listed (kept): {counts.Delisted}");
        }
    }

    internal static TaxSource RequireTax(Arguments args)
    {
        var code = args.Get("tax") ?? throw new ArgumentException("Missing --tax (supported: " + SupportedTaxes() + ").");
        return TaxSource.Find(code) ?? throw new ArgumentException($"Unsupported tax '{code}' (supported: {SupportedTaxes()}).");
    }

    private static string SupportedTaxes() => string.Join(", ", TaxSource.All.Select(s => s.Code));

    internal static string CorpusRoot(Arguments args) => Path.Combine(DataDirectory.Resolve(args.Get("data-dir")), "corpus");
}
