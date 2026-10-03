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
          crawl --tax T | --all      Download the listed rulings (polite, resumable) and extract their text;
                                     --all crawls every supported tax, one after another.
              --interval-seconds N   Gap between requests, at least 1 (default 1).
              --max-retries N        Retries on 429/5xx/network errors, 0 to 10 (default 4).
              --retry-failed         Retry rulings that failed in an earlier run.
              --use-cached-listing   Do not request the listing again; use the cached response.
              --max-downloads N      Stop after N PDF downloads (over the whole run with --all).
          extract --tax T | --all    Extract the text of every cached PDF again (offline, no requests).
          corpus-status --tax T | --all
                                     Print corpus counts per tax; exits 0 only when no listed ruling is pending.
              --list-failed          Also list failed rulings with their reason.
          model download             Download the pinned embedding model into data/models (SHA-256 verified).
          index [--tax T]            Chunk and embed the extracted rulings of every crawled tax into
                                     data/lupa-fiscal.db (idempotent, resumable; downloads the model first if
                                     needed). A PDF already listed by an earlier tax is stored once.
                                     --tax T only embeds the rulings that tax lists.
          index-status [--tax T]     Print index counts per tax; exits 0 only when every extracted ruling is
                                     indexed and every chunk has a vector.
          search "question"          Print the best passages with ruling metadata, source URL and elapsed ms.
              --tax T --year Y       Filters (applied to keyword and vector results).
              --article A            Article within the --tax code (requires --tax).
              --limit N              Results, 1 to 50 (default 10).
              --mode M               hybrid (default), keyword or vector.
          eval --questions FILE      Recall@10 and MRR@10 at ruling level for keyword, vector and hybrid search;
                                     prints them and writes nothing unless --record or --freeze is given.
              --out FILE             Report path (default docs/eval/report.md in the repository); its
                                     history.json sits in the same folder.
              --min-recall R         Exit 1 when hybrid recall@10 is below R (0 to 1).
              --record               Record the scores in the report and its history.
              --label L --note N     Name and describe the iteration being recorded (with --record).
              --freeze               Record only the question set's hash, before any measurement.
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
        Func<IEmbedder>? embedderFactory = null, ICrawlClock? crawlClock = null)
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
                "crawl" => await CrawlAsync(parsed, stdout, stderr, handlerFactory, crawlClock, cancellationToken),
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
        Func<HttpMessageHandler>? handlerFactory, ICrawlClock? clock, CancellationToken cancellationToken)
    {
        var sources = RequireTaxes(args);
        args.EnsureOnly("tax", "all", "data-dir", "interval-seconds", "max-retries", "retry-failed", "use-cached-listing", "max-downloads");

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

        var root = CorpusRoot(args);
        var stores = sources.Select(source => new CorpusStore(root, source)).ToList();
        using var log = new CrawlLog(stdout);
        log.WriteLine($"Crawl {string.Join(", ", sources.Select(s => s.Code))} into {root}; interval {options.RequestInterval.TotalSeconds:0.###} s, max retries {options.MaxRetries}.");

        // One client for the whole run: one request at a time, at least the interval apart, across taxes.
        using var client = new PoliteHttpClient(handlerFactory?.Invoke() ?? PoliteHttpClient.CreateDefaultHandler(),
            options, clock ?? SystemCrawlClock.Instance, log);
        var crawler = new MultiTaxCrawler(client, stores, new PdfTextExtractor(), options, log.Switch);
        var run = new CrawlRunOptions
        {
            RetryFailed = args.Has("retry-failed"),
            UseCachedListing = args.Has("use-cached-listing"),
            MaxDownloads = maxDownloads,
        };

        MultiTaxCrawlSummary summary;
        try
        {
            summary = await crawler.RunAsync(run, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            log.WriteLine($"Crawl interrupted after {client.RequestCount} request(s); progress is saved, run crawl again to resume.");
            return ExitFailure;
        }

        for (var i = 0; i < stores.Count; i++)
        {
            var tax = summary.Taxes[i];
            var taxLog = log.Switch(stores[i]);
            if (tax.Summary is { } crawled)
            {
                taxLog.WriteLine($"{tax.Source.Code}: {crawled.Requests} request(s), {crawled.Downloads} PDF download(s).");
            }
            if (tax.Counts is { } counts) WriteCounts(taxLog, tax.Source, counts);
            else taxLog.WriteLine($"{tax.Source.Code}: no corpus yet.");
        }
        new TimestampedWriter(stdout).WriteLine($"Crawl finished: {summary.Requests} request(s), {summary.Downloads} PDF download(s).");
        if (summary.Aborted)
        {
            stderr.WriteLine($"Crawl stopped: {summary.AbortReason}.");
            return ExitFailure;
        }
        return summary.IsComplete ? ExitOk : ExitFailure;
    }

    private static int Extract(Arguments args, TextWriter stdout, CancellationToken cancellationToken)
    {
        var sources = RequireTaxes(args);
        args.EnsureOnly("tax", "all", "data-dir");
        var root = CorpusRoot(args);
        var stores = sources.Select(source => new CorpusStore(root, source)).ToList();
        if (!stores.Any(store => File.Exists(store.ManifestPath)))
        {
            throw new ArgumentException(stores.Count == 1
                ? $"No corpus for {stores[0].Source.Code} at {stores[0].TaxDirectory}. Run: crawl --tax {stores[0].Source.Code}"
                : $"No corpus under {root}. Run: crawl --all");
        }

        var log = new TimestampedWriter(stdout);
        var complete = true;
        foreach (var store in stores)
        {
            if (!File.Exists(store.ManifestPath))
            {
                log.WriteLine($"No corpus for {store.Source.Code} at {store.TaxDirectory}; skipped. Run: crawl --tax {store.Source.Code}");
                complete = false;
                continue;
            }
            var summary = new RulingTextExtraction(store, new PdfTextExtractor(), log).ReextractAll(cancellationToken);
            log.WriteLine($"Extracted {summary.Processed} cached PDF(s) again.");
            WriteCounts(log, store.Source, summary.Counts);
            complete &= summary.Counts.IsComplete;
        }
        return complete ? ExitOk : ExitFailure;
    }

    private static int CorpusStatus(Arguments args, TextWriter stdout, TextWriter stderr)
    {
        var sources = RequireTaxes(args);
        args.EnsureOnly("tax", "all", "data-dir", "list-failed");
        var root = CorpusRoot(args);
        var listFailed = args.Has("list-failed");
        if (!args.Has("all"))
        {
            return TaxStatus(new CorpusStore(root, sources[0]), listFailed, stdout, stderr) is { IsComplete: true }
                ? ExitOk
                : ExitFailure;
        }

        var all = sources.Select(source => TaxStatus(new CorpusStore(root, source), listFailed, stdout, stdout)).ToList();
        var crawled = all.OfType<CorpusCounts>().ToList();
        stdout.WriteLine();
        WriteCounts(stdout, $"All {sources.Count} taxes ({crawled.Count} crawled)", CorpusCounts.Total(crawled));
        var incomplete = sources.Where((_, i) => all[i] is not { IsComplete: true }).Select(s => s.Code).ToList();
        if (incomplete.Count == 0)
        {
            stdout.WriteLine("Status: complete (no listed ruling of any supported tax is pending).");
            return ExitOk;
        }
        stdout.WriteLine($"Status: incomplete ({string.Join(", ", incomplete)}). Run: crawl --all");
        return ExitFailure;
    }

    /// <summary>Prints the status of one tax; returns its counts, or null when it has no corpus.</summary>
    private static CorpusCounts? TaxStatus(CorpusStore store, bool listFailed, TextWriter stdout, TextWriter stderr)
    {
        var source = store.Source;
        var manifest = store.LoadManifest();
        if (manifest is null)
        {
            stderr.WriteLine($"No corpus for {source.Code} at {store.TaxDirectory}. Run: crawl --tax {source.Code}");
            return null;
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
        if (listFailed)
        {
            foreach (var ruling in manifest.Rulings.Where(r => r.Listed && r.State == RulingState.Failed))
            {
                stdout.WriteLine($"  failed {ruling.Id}: {ruling.Reason}");
            }
        }

        stdout.WriteLine(counts.IsComplete
            ? "Status: complete (no listed ruling is pending)."
            : $"Status: incomplete ({counts.Pending + counts.Downloaded} listed ruling(s) still pending). Run: crawl --tax {source.Code}");
        return counts;
    }

    private static void WriteCounts(TextWriter writer, TaxSource source, CorpusCounts counts) =>
        WriteCounts(writer, source.Code, counts);

    private static void WriteCounts(TextWriter writer, string label, CorpusCounts counts)
    {
        writer.WriteLine($"{label} listed: {counts.Listed}");
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
        var code = args.Get("tax") ?? throw new ArgumentException("Missing --tax or --all (supported: " + SupportedTaxes() + ").");
        return TaxSource.Find(code) ?? throw new ArgumentException($"Unsupported tax '{code}' (supported: {SupportedTaxes()}).");
    }

    /// <summary>The tax given with --tax, or every supported tax with --all (exactly one of the two).</summary>
    internal static IReadOnlyList<TaxSource> RequireTaxes(Arguments args)
    {
        if (!args.Has("all")) return [RequireTax(args)];
        if (args.Has("tax")) throw new ArgumentException("Use either --tax or --all, not both.");
        return TaxSource.All;
    }

    private static string SupportedTaxes() => string.Join(", ", TaxSource.All.Select(s => s.Code));

    internal static string CorpusRoot(Arguments args) => Path.Combine(DataDirectory.Resolve(args.Get("data-dir")), "corpus");
}
