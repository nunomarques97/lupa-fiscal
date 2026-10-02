using System.Net;
using LupaFiscal.Core.Corpus;
using LupaFiscal.Core.Crawling;
using LupaFiscal.Core.Extraction;
using LupaFiscal.Tests.Support;

namespace LupaFiscal.Tests;

public sealed class CorpusCrawlerTests : IDisposable
{
    private static readonly string ListingUrl = TaxSource.Cirs.ListingUri.AbsoluteUri;

    private readonly TempDirectory _temp = new();
    private readonly CorpusStore _store;

    public CorpusCrawlerTests() => _store = new CorpusStore(_temp.Path, TaxSource.Cirs);

    public void Dispose() => _temp.Dispose();

    private static FakeHandler Site(FakeClock clock, string listingJson, params int[] pdfNumbers)
    {
        var handler = new FakeHandler(clock);
        handler.Always(TestListing.RobotsUrl, () => FakeHandler.Status(HttpStatusCode.NotFound));
        handler.Always(ListingUrl, () => FakeHandler.Text(listingJson, "application/json"));
        foreach (var number in pdfNumbers)
        {
            handler.Always(TestListing.PdfUrl($"PIV_{number}.pdf"), () => FakeHandler.Bytes(TestPdfs.Ruling()));
        }
        return handler;
    }

    private async Task<CrawlSummary> Crawl(FakeHandler handler, FakeClock clock, CrawlRunOptions? run = null,
        CancellationToken cancellationToken = default, CrawlerOptions? options = null)
    {
        options ??= new CrawlerOptions();
        using var client = new PoliteHttpClient(handler, options, clock, TextWriter.Null);
        var crawler = new CorpusCrawler(client, _store, new PdfTextExtractor(), options, TextWriter.Null);
        return await crawler.RunAsync(run ?? new CrawlRunOptions(), cancellationToken);
    }

    private int PdfRequests(FakeHandler handler) =>
        handler.Requests.Count(r => r.Url.AbsolutePath.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase));

    [Fact]
    public async Task CrawlsListsDownloadsAndExtractsEveryRuling()
    {
        var clock = new FakeClock();
        var handler = Site(clock, TestListing.ForNumbers(1, 2, 3), 1, 2, 3);

        var summary = await Crawl(handler, clock);

        Assert.False(summary.Aborted);
        Assert.Equal(new CorpusCounts(3, 0, 0, 3, 0, 0, 0), summary.Counts);
        Assert.True(summary.Counts.IsComplete);
        Assert.Equal(5, handler.Requests.Count);
        Assert.Equal(TestListing.RobotsUrl, handler.Requests[0].Url.AbsoluteUri);
        Assert.Equal(ListingUrl, handler.Requests[1].Url.AbsoluteUri);
        Assert.True(File.Exists(_store.PdfPath("piv_1")));
        Assert.Contains(TestPdfs.Expect("III. INFORMAÇÃO"), File.ReadAllText(_store.TextPath("piv_1")), StringComparison.Ordinal);
        Assert.NotNull(_store.ReadListing());

        var ruling = _store.LoadManifest()!.Rulings.Single(r => r.Id == "piv_1");
        Assert.Equal(RulingState.Extracted, ruling.State);
        Assert.Equal("CIRS", ruling.Tax);
        Assert.Equal("10", ruling.Article);
        Assert.Equal(new DateOnly(2026, 9, 23), ruling.PublishedOn);
        Assert.Equal(new DateOnly(2026, 9, 22), ruling.DecisionDate);
        Assert.Equal("1", ruling.ProcessNumber);
        Assert.Equal("Assunto de teste", ruling.Subject);
        Assert.Equal(TestListing.PdfUrl("PIV_1.pdf"), ruling.SourceUrl);
        Assert.NotNull(ruling.PdfSha256);
    }

    [Fact]
    public async Task ResumesAfterAnInterruptionWithoutDownloadingCachedPdfsAgain()
    {
        var listing = TestListing.ForNumbers(1, 2, 3, 4);
        var clock = new FakeClock();
        var first = Site(clock, listing, 1, 2, 3, 4);
        using var interruption = new CancellationTokenSource();
        var pdfsSeen = 0;
        first.OnRequest = url =>
        {
            if (url.AbsolutePath.EndsWith(".pdf", StringComparison.Ordinal) && ++pdfsSeen == 2) interruption.Cancel();
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Crawl(first, clock, cancellationToken: interruption.Token));

        var afterInterruption = CorpusCounts.From(_store.LoadManifest()!);
        Assert.Equal(4, afterInterruption.Listed);
        Assert.Equal(1, afterInterruption.Extracted);
        Assert.Equal(3, afterInterruption.Pending);
        Assert.False(afterInterruption.IsComplete);

        var second = Site(clock, listing, 1, 2, 3, 4);
        var summary = await Crawl(second, clock);

        Assert.True(summary.Counts.IsComplete);
        Assert.Equal(4, summary.Counts.Extracted);
        Assert.Equal(0, second.CountFor(TestListing.PdfUrl("PIV_1.pdf")));
        Assert.Equal(3, PdfRequests(second));

        var third = Site(clock, listing, 1, 2, 3, 4);
        await Crawl(third, clock);
        Assert.Equal(0, PdfRequests(third));
    }

    [Fact]
    public async Task CachedPdfWithoutRecordedStateIsNotRequested()
    {
        _store.WritePdf("piv_2", TestPdfs.Ruling());
        var clock = new FakeClock();
        var handler = Site(clock, TestListing.ForNumbers(1, 2), 1, 2);

        var summary = await Crawl(handler, clock);

        Assert.Equal(0, handler.CountFor(TestListing.PdfUrl("PIV_2.pdf")));
        Assert.Equal(2, summary.Counts.Extracted);
    }

    [Fact]
    public async Task RetriesThrottledPdfDownloadsAndRecordsFinalFailures()
    {
        var clock = new FakeClock();
        var handler = Site(clock, TestListing.ForNumbers(1, 2, 3), 1);
        var pdf2 = TestListing.PdfUrl("PIV_2.pdf");
        handler.Then(pdf2, () => FakeHandler.Status(HttpStatusCode.TooManyRequests, TimeSpan.FromSeconds(20)));
        handler.Then(pdf2, () => FakeHandler.Status(HttpStatusCode.ServiceUnavailable));
        handler.Then(pdf2, () => FakeHandler.Bytes(TestPdfs.Ruling()));
        handler.Always(TestListing.PdfUrl("PIV_3.pdf"), () => FakeHandler.Status(HttpStatusCode.ServiceUnavailable));

        var summary = await Crawl(handler, clock, options: new CrawlerOptions { MaxRetries = 2 });

        Assert.Equal(3, handler.CountFor(pdf2));
        Assert.Equal(3, handler.CountFor(TestListing.PdfUrl("PIV_3.pdf")));
        Assert.Contains(TimeSpan.FromSeconds(20), clock.Delays);
        Assert.Equal(2, summary.Counts.Extracted);
        Assert.Equal(1, summary.Counts.Failed);
        Assert.True(summary.Counts.IsComplete);
        var failed = _store.LoadManifest()!.Rulings.Single(r => r.State == RulingState.Failed);
        Assert.Equal("piv_3", failed.Id);
        Assert.Contains("503", failed.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StopsWhenTheSiteLooksUnavailableAndLeavesThoseRulingsPending()
    {
        var clock = new FakeClock();
        var handler = Site(clock, TestListing.ForNumbers(1, 2, 3, 4, 5), 1);
        foreach (var number in new[] { 2, 3, 4, 5 })
        {
            handler.Always(TestListing.PdfUrl($"PIV_{number}.pdf"), () => FakeHandler.Status(HttpStatusCode.BadGateway));
        }

        var summary = await Crawl(handler, clock, options: new CrawlerOptions { MaxRetries = 1 });

        Assert.True(summary.Aborted);
        Assert.Equal(1, summary.Counts.Extracted);
        Assert.Equal(4, summary.Counts.Pending);
        Assert.Equal(0, summary.Counts.Failed);
        Assert.Equal(2, handler.CountFor(TestListing.PdfUrl("PIV_4.pdf")));
        Assert.Equal(0, handler.CountFor(TestListing.PdfUrl("PIV_5.pdf")));
    }

    [Fact]
    public async Task FailedRulingsAreRetriedOnlyWhenAsked()
    {
        var clock = new FakeClock();
        var failing = Site(clock, TestListing.ForNumbers(1));
        await Crawl(failing, clock);
        Assert.Equal(1, CorpusCounts.From(_store.LoadManifest()!).Failed);

        var again = Site(clock, TestListing.ForNumbers(1), 1);
        await Crawl(again, clock);
        Assert.Equal(0, PdfRequests(again));

        var retry = Site(clock, TestListing.ForNumbers(1), 1);
        var summary = await Crawl(retry, clock, new CrawlRunOptions { RetryFailed = true });
        Assert.Equal(1, PdfRequests(retry));
        Assert.Equal(1, summary.Counts.Extracted);
    }

    [Fact]
    public async Task RobotsDisallowedPdfIsNeverRequestedAndIsRecordedAsFailed()
    {
        var clock = new FakeClock();
        var handler = Site(clock, TestListing.ForNumbers(1, 2), 1, 2);
        handler.Always(TestListing.RobotsUrl, () => FakeHandler.Text($"User-agent: lupafiscal\nDisallow: {TestListing.DocumentsPath}PIV_2.pdf"));

        var summary = await Crawl(handler, clock);

        Assert.Equal(0, handler.CountFor(TestListing.PdfUrl("PIV_2.pdf")));
        Assert.Equal(1, summary.Counts.Failed);
        var failed = _store.LoadManifest()!.Rulings.Single(r => r.Id == "piv_2");
        Assert.Contains("robots.txt", failed.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnreachableRobotsStopsTheCrawlBeforeTheListing()
    {
        var clock = new FakeClock();
        var handler = Site(clock, TestListing.ForNumbers(1), 1);
        handler.Always(TestListing.RobotsUrl, () => FakeHandler.Status(HttpStatusCode.InternalServerError));

        var summary = await Crawl(handler, clock, options: new CrawlerOptions { MaxRetries = 1 });

        Assert.True(summary.Aborted);
        Assert.Equal(0, handler.CountFor(ListingUrl));
        Assert.Equal(0, PdfRequests(handler));
    }

    [Fact]
    public async Task PathTraversalFileNamesStayInsideTheCorpusDirectory()
    {
        var href = TestListing.DocumentsPath + "..%5C..%5C..%5C..%5Cevil.pdf";
        var json = TestListing.Json(TestListing.Row(href, "9"));
        var clock = new FakeClock();
        var handler = Site(clock, json);
        handler.Always(new Uri(new Uri(TestListing.Origin), href).AbsoluteUri, () => FakeHandler.Bytes(TestPdfs.Ruling()));

        var summary = await Crawl(handler, clock);

        Assert.Equal(1, summary.Counts.Extracted);
        var ruling = Assert.Single(_store.LoadManifest()!.Rulings);
        Assert.Equal("evil", ruling.Id);
        var root = Path.GetFullPath(_temp.Path) + Path.DirectorySeparatorChar;
        Assert.All(Directory.EnumerateFiles(_temp.Path, "*", SearchOption.AllDirectories),
            file => Assert.StartsWith(root + "cirs" + Path.DirectorySeparatorChar, Path.GetFullPath(file)));
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(_temp.Path)!, "evil.pdf")));
    }

    [Fact]
    public async Task ListingLinksToOtherHostsAreNeverRequested()
    {
        var json = TestListing.Json(
            TestListing.Row("https://evil.example/PIV_1.pdf", "1"),
            TestListing.Row(TestListing.DocumentsPath + "PIV_2.pdf", "2"));
        var clock = new FakeClock();
        var handler = Site(clock, json, 2);

        var summary = await Crawl(handler, clock);

        Assert.Equal(1, summary.Counts.Listed);
        Assert.DoesNotContain(handler.Requests, r => r.Url.Host != "info.portaldasfinancas.gov.pt");
    }

    [Fact]
    public async Task OversizedAndNonPdfResponsesFail()
    {
        var clock = new FakeClock();
        var handler = Site(clock, TestListing.ForNumbers(1, 2));
        handler.Always(TestListing.PdfUrl("PIV_1.pdf"), () => FakeHandler.Bytes(new byte[4096]));
        handler.Always(TestListing.PdfUrl("PIV_2.pdf"), () => FakeHandler.Text("<html>error</html>", "text/html"));

        var summary = await Crawl(handler, clock, options: new CrawlerOptions { MaxPdfBytes = 1024 });

        Assert.Equal(2, summary.Counts.Failed);
        var reasons = _store.LoadManifest()!.Rulings.ToDictionary(r => r.Id, r => r.Reason);
        Assert.Contains("cap", reasons["piv_1"], StringComparison.Ordinal);
        Assert.Contains("not a PDF", reasons["piv_2"], StringComparison.Ordinal);
        Assert.False(_store.HasPdf("piv_1"));
        Assert.False(_store.HasPdf("piv_2"));
    }

    [Fact]
    public async Task ScannedPdfsAreSkippedAndReported()
    {
        var clock = new FakeClock();
        var handler = Site(clock, TestListing.ForNumbers(1, 2, 3), 1);
        handler.Always(TestListing.PdfUrl("PIV_2.pdf"), () => FakeHandler.Bytes(TestPdfs.ImageOnly()));
        handler.Always(TestListing.PdfUrl("PIV_3.pdf"), () => FakeHandler.Bytes(TestPdfs.EmptyPages()));

        var summary = await Crawl(handler, clock);

        Assert.Equal(1, summary.Counts.Extracted);
        Assert.Equal(2, summary.Counts.ScannedSkipped);
        Assert.True(summary.Counts.IsComplete);
        Assert.False(File.Exists(_store.TextPath("piv_2")));
        var report = File.ReadAllText(_store.ScannedReportPath);
        Assert.Contains("piv_2", report, StringComparison.Ordinal);
        Assert.Contains("piv_3", report, StringComparison.Ordinal);
        Assert.DoesNotContain("piv_1 ", report, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FallsBackToTheCachedListingWhenTheListingRequestFails()
    {
        var clock = new FakeClock();
        await Crawl(Site(clock, TestListing.ForNumbers(1), 1), clock);

        var handler = Site(clock, "", 1);
        handler.Always(ListingUrl, () => FakeHandler.Status(HttpStatusCode.Forbidden));
        var summary = await Crawl(handler, clock);

        Assert.False(summary.Aborted);
        Assert.Equal(1, summary.Counts.Listed);
        Assert.Equal(0, PdfRequests(handler));
    }

    [Fact]
    public async Task NoListingAndNoCacheAbortsTheCrawl()
    {
        var clock = new FakeClock();
        var handler = Site(clock, "");
        handler.Always(ListingUrl, () => FakeHandler.Status(HttpStatusCode.Forbidden));

        var summary = await Crawl(handler, clock);

        Assert.True(summary.Aborted);
        Assert.Null(_store.ReadListing());
    }

    [Fact]
    public async Task UseCachedListingSkipsTheListingRequest()
    {
        var clock = new FakeClock();
        await Crawl(Site(clock, TestListing.ForNumbers(1), 1), clock);

        var handler = Site(clock, TestListing.ForNumbers(1), 1);
        await Crawl(handler, clock, new CrawlRunOptions { UseCachedListing = true });

        Assert.Equal(0, handler.CountFor(ListingUrl));
    }

    [Fact]
    public async Task RulingsDroppedFromTheListingAreKeptButNotCounted()
    {
        var clock = new FakeClock();
        await Crawl(Site(clock, TestListing.ForNumbers(1, 2), 1, 2), clock);

        var summary = await Crawl(Site(clock, TestListing.ForNumbers(2), 2), clock);

        Assert.Equal(1, summary.Counts.Listed);
        Assert.Equal(1, summary.Counts.Delisted);
        Assert.True(summary.Counts.IsComplete);
    }

    [Fact]
    public async Task ProcessNumberComesFromThePdfWhenTheListingHasNone()
    {
        var json = TestListing.Json(TestListing.Row(TestListing.DocumentsPath + "PIV_5.pdf", ""));
        var clock = new FakeClock();
        var handler = Site(clock, json, 5);

        await Crawl(handler, clock);

        Assert.Equal("31329", _store.LoadManifest()!.Rulings.Single().ProcessNumber);
    }

    [Fact]
    public async Task ListingProcessNumberReplacesOneTakenFromThePdf()
    {
        var clock = new FakeClock();
        var withoutNumber = TestListing.Json(TestListing.Row(TestListing.DocumentsPath + "PIV_5.pdf", ""));
        await Crawl(Site(clock, withoutNumber, 5), clock);
        Assert.True(_store.LoadManifest()!.Rulings.Single().ProcessNumberFromPdf);

        var withNumber = TestListing.Json(TestListing.Row(TestListing.DocumentsPath + "PIV_5.pdf", "777"));
        await Crawl(Site(clock, withNumber, 5), clock);
        var ruling = _store.LoadManifest()!.Rulings.Single();
        Assert.Equal("777", ruling.ProcessNumber);
        Assert.False(ruling.ProcessNumberFromPdf);

        new RulingTextExtraction(_store, new PdfTextExtractor(), TextWriter.Null).ReextractAll(CancellationToken.None);
        Assert.Equal("777", _store.LoadManifest()!.Rulings.Single().ProcessNumber);
    }

    [Fact]
    public async Task ReextractionRebuildsTextFromTheCacheWithoutRequests()
    {
        var clock = new FakeClock();
        await Crawl(Site(clock, TestListing.ForNumbers(1, 2), 1, 2), clock);
        File.Delete(_store.TextPath("piv_1"));

        var summary = new RulingTextExtraction(_store, new PdfTextExtractor(), TextWriter.Null).ReextractAll(CancellationToken.None);

        Assert.Equal(2, summary.Processed);
        Assert.Equal(2, summary.Counts.Extracted);
        Assert.True(summary.Counts.IsComplete);
        Assert.True(File.Exists(_store.TextPath("piv_1")));
    }

    [Fact]
    public async Task MaxDownloadsLimitsARun()
    {
        var clock = new FakeClock();
        var handler = Site(clock, TestListing.ForNumbers(1, 2, 3), 1, 2, 3);

        var summary = await Crawl(handler, clock, new CrawlRunOptions { MaxDownloads = 1 });

        Assert.Equal(1, PdfRequests(handler));
        Assert.Equal(2, summary.Counts.Pending);
        Assert.False(summary.Counts.IsComplete);
    }
}
