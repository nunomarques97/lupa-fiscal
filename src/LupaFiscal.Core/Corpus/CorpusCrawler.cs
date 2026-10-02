using System.Security.Cryptography;
using System.Text;
using LupaFiscal.Core.Crawling;
using LupaFiscal.Core.Extraction;

namespace LupaFiscal.Core.Corpus;

public sealed record CrawlRunOptions
{
    /// <summary>Use the cached listing response instead of requesting it again.</summary>
    public bool UseCachedListing { get; init; }

    /// <summary>Retry rulings that failed in an earlier run.</summary>
    public bool RetryFailed { get; init; }

    /// <summary>Stop after this many PDF downloads (null = no limit).</summary>
    public int? MaxDownloads { get; init; }
}

public sealed record CrawlSummary(CorpusCounts Counts, int Requests, int Downloads, string? AbortReason)
{
    public bool Aborted => AbortReason is not null;
}

/// <summary>
/// Crawls one tax: robots.txt, the listing, then every listed PDF that is not cached yet, and
/// extracts each PDF right after download. State is saved to the manifest as it changes, so an
/// interrupted run resumes where it stopped and never downloads a cached PDF again.
/// </summary>
public sealed class CorpusCrawler(
    PoliteHttpClient client,
    CorpusStore store,
    PdfTextExtractor extractor,
    CrawlerOptions options,
    TextWriter log,
    Func<DateTimeOffset>? now = null)
{
    private const int SaveEvery = 10;

    /// <summary>
    /// Consecutive downloads that still fail after their retries (429, 5xx, network) before the
    /// run stops: the site is treated as unavailable rather than hammered for every ruling.
    /// </summary>
    public const int MaxConsecutiveUnavailable = 3;

    private readonly Func<DateTimeOffset> _now = now ?? (() => DateTimeOffset.UtcNow);

    private readonly RulingTextExtraction _extraction = new(store, extractor, log, now);

    public async Task<CrawlSummary> RunAsync(CrawlRunOptions run, CancellationToken cancellationToken)
    {
        var source = store.Source;
        var manifest = store.LoadManifest() ?? new CorpusManifest { Tax = source.Code };

        var robots = await client.GetRobotsAsync(source.Origin, cancellationToken);
        if (robots.BlocksEverything)
        {
            return Abort(manifest, "robots.txt could not be read, so nothing may be crawled");
        }

        var listing = await LoadListingAsync(run, manifest, cancellationToken);
        if (listing is null)
        {
            return Abort(manifest, "no listing available (request failed and no cached listing)");
        }

        Merge(manifest, listing.Entries);
        foreach (var rejected in listing.Rejected)
        {
            log.WriteLine($"Listing entry skipped: {rejected}");
        }
        store.SaveManifest(manifest);
        log.WriteLine($"Listing: {listing.Entries.Count} rulings ({listing.Rejected.Count} rows skipped).");

        var downloads = 0;
        var unsaved = 0;
        string? abortReason = null;
        var unavailableStreak = new List<RulingRecord>();
        try
        {
            foreach (var ruling in manifest.Rulings.Where(r => r.Listed).ToList())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var wanted = ruling.State is RulingState.Pending or RulingState.Downloaded
                    || (run.RetryFailed && ruling.State == RulingState.Failed);
                if (!wanted) continue;

                if (ruling.State != RulingState.Downloaded)
                {
                    if (!store.HasPdf(ruling.Id))
                    {
                        if (run.MaxDownloads is { } max && downloads >= max) continue;
                        downloads++;
                        if (await DownloadAsync(ruling, cancellationToken))
                        {
                            unavailableStreak.Add(ruling);
                            if (unavailableStreak.Count >= MaxConsecutiveUnavailable)
                            {
                                // Likely an outage, not bad rulings: leave them pending for the next run.
                                foreach (var unavailable in unavailableStreak) unavailable.State = RulingState.Pending;
                                abortReason = $"the site looks unavailable ({MaxConsecutiveUnavailable} downloads in a row failed after retries); run crawl again later";
                                break;
                            }
                        }
                        else
                        {
                            unavailableStreak.Clear();
                        }
                    }
                    else
                    {
                        MarkDownloaded(ruling, store.ReadPdf(ruling.Id));
                    }
                }

                if (ruling.State == RulingState.Downloaded)
                {
                    Extract(ruling);
                }

                if (++unsaved >= SaveEvery)
                {
                    store.SaveManifest(manifest);
                    unsaved = 0;
                }
            }
        }
        finally
        {
            store.SaveManifest(manifest);
            store.WriteScannedReport(manifest.Rulings.Where(r => r.Listed));
        }

        if (abortReason is not null) log.WriteLine($"Crawl stopped: {abortReason}.");
        return new CrawlSummary(CorpusCounts.From(manifest), client.RequestCount, downloads, abortReason);
    }

    private CrawlSummary Abort(CorpusManifest manifest, string reason)
    {
        log.WriteLine($"Crawl stopped: {reason}.");
        return new CrawlSummary(CorpusCounts.From(manifest), client.RequestCount, 0, reason);
    }

    private async Task<ListingParseResult?> LoadListingAsync(CrawlRunOptions run, CorpusManifest manifest,
        CancellationToken cancellationToken)
    {
        var source = store.Source;
        var cached = store.ReadListing();
        if (run.UseCachedListing && cached is not null)
        {
            log.WriteLine("Using the cached listing response.");
            return ListingParser.Parse(cached, source, client.Policy);
        }

        string? failure;
        try
        {
            var response = await client.GetAsync(source.ListingUri, "application/json", options.MaxListingBytes, cancellationToken);
            if (response.IsSuccess)
            {
                var json = Encoding.UTF8.GetString(response.Body);
                var parsed = ListingParser.Parse(json, source, client.Policy);
                store.WriteListing(json);
                manifest.ListingFetchedAt = _now();
                return parsed;
            }
            failure = $"HTTP {(int)response.Status}";
        }
        catch (Exception ex) when (ex is CrawlFetchException or CrawlPolicyException or FormatException or System.Text.Json.JsonException)
        {
            failure = ex.Message;
        }

        log.WriteLine($"Listing request failed: {failure}.");
        if (cached is null) return null;
        log.WriteLine("Falling back to the cached listing response.");
        return ListingParser.Parse(cached, source, client.Policy);
    }

    private void Merge(CorpusManifest manifest, IReadOnlyList<ListingEntry> entries)
    {
        var existing = manifest.Rulings.ToDictionary(r => r.Id, StringComparer.Ordinal);
        var merged = new List<RulingRecord>(entries.Count);
        foreach (var entry in entries)
        {
            if (!existing.Remove(entry.Id, out var ruling))
            {
                ruling = new RulingRecord
                {
                    Id = entry.Id,
                    FileName = entry.FileName,
                    SourceUrl = entry.SourceUrl,
                    Tax = entry.Tax,
                    UpdatedAt = _now(),
                };
            }

            ruling.FileName = entry.FileName;
            ruling.SourceUrl = entry.SourceUrl;
            ruling.Tax = entry.Tax;
            ruling.Diploma = entry.Diploma;
            ruling.Article = entry.Article;
            ruling.Paragraph = entry.Paragraph;
            ruling.PublishedOn = entry.PublishedOn;
            ruling.Subject = entry.Subject;
            if (entry.ProcessNumber.Length > 0)
            {
                ruling.ProcessNumber = entry.ProcessNumber;
                ruling.ProcessNumberFromPdf = false;
            }
            else if (!ruling.ProcessNumberFromPdf)
            {
                // Filled from the PDF text during extraction.
                ruling.ProcessNumber = "";
            }
            ruling.Listed = true;
            merged.Add(ruling);
        }

        foreach (var delisted in existing.Values)
        {
            delisted.Listed = false;
            merged.Add(delisted);
        }
        manifest.Rulings = merged;
    }

    /// <summary>Downloads one PDF. Returns true when it failed because the site was unavailable.</summary>
    private async Task<bool> DownloadAsync(RulingRecord ruling, CancellationToken cancellationToken)
    {
        try
        {
            var url = new Uri(ruling.SourceUrl);
            var response = await client.GetAsync(url, "application/pdf", options.MaxPdfBytes, cancellationToken);
            if (!response.IsSuccess)
            {
                Fail(ruling, $"HTTP {(int)response.Status}");
                return false;
            }
            if (!response.Body.AsSpan().StartsWith("%PDF-"u8))
            {
                Fail(ruling, $"response is not a PDF ({response.ContentType ?? "no content type"})");
                return false;
            }
            store.WritePdf(ruling.Id, response.Body);
            MarkDownloaded(ruling, response.Body);
            return false;
        }
        catch (CrawlFetchException ex)
        {
            Fail(ruling, ex.Message);
            return true;
        }
        catch (Exception ex) when (ex is CrawlPolicyException or UriFormatException)
        {
            Fail(ruling, ex.Message);
            return false;
        }
    }

    private void MarkDownloaded(RulingRecord ruling, byte[] pdf)
    {
        ruling.State = RulingState.Downloaded;
        ruling.Reason = null;
        ruling.PdfBytes = pdf.LongLength;
        ruling.PdfSha256 = Convert.ToHexStringLower(SHA256.HashData(pdf));
        ruling.UpdatedAt = _now();
    }

    private void Extract(RulingRecord ruling) => _extraction.Extract(ruling);

    private void Fail(RulingRecord ruling, string reason)
    {
        ruling.State = RulingState.Failed;
        ruling.Reason = reason;
        ruling.UpdatedAt = _now();
        log.WriteLine($"{ruling.Id}: failed, {reason}.");
    }
}
