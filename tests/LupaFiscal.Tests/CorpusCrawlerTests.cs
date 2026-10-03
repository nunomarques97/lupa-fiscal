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

    // Multi-tax runs: CIRS (PIV_1, PIV_2) then CIVA (PIV_1 with other content, PIV_95001 byte-identical
    // to CIRS PIV_2, PIV_95003 scanned).
    private static readonly TaxSource Civa = TaxSource.Find("CIVA")!;
    private static readonly string CivaListingUrl = Civa.ListingUri.AbsoluteUri;
    private static readonly byte[] SharedPdf = TestPdfs.WithText([.. TestPdfs.RulingLines, "Conclusão: a mesma ficha consta de duas listas."]);
    private static readonly byte[] IvaPdf = TestPdfs.WithText(
        "INFORMAÇÃO VINCULATIVA",
        "Diploma: CIVA",
        "Assunto: Isenção de IVA na locação de imóveis",
        "Processo: 90002, com despacho de 2024-10-28, da Diretora de Serviços",
        "A requerente pretende saber se o arrendamento de um armazém está isento de imposto.",
        "A locação de imóveis está isenta, salvo renúncia à isenção nos termos da lei aplicável.");

    private string CivaPdf(string fileName) => TestListing.PdfUrlFor(Civa, fileName);

    private FakeHandler TwoTaxSite(FakeClock clock)
    {
        var handler = Site(clock, TestListing.ForNumbers(1, 2), 1);
        handler.Always(TestListing.PdfUrl("PIV_2.pdf"), () => FakeHandler.Bytes(SharedPdf));
        handler.Always(CivaListingUrl, () => FakeHandler.Text(TestListing.ForNumbers(Civa, 1, 95001, 95003), "application/json"));
        handler.Always(CivaPdf("PIV_1.pdf"), () => FakeHandler.Bytes(IvaPdf));
        handler.Always(CivaPdf("PIV_95001.pdf"), () => FakeHandler.Bytes(SharedPdf));
        handler.Always(CivaPdf("PIV_95003.pdf"), () => FakeHandler.Bytes(TestPdfs.ImageOnly()));
        return handler;
    }

    private CorpusStore CivaStore => new(_temp.Path, Civa);

    private async Task<MultiTaxCrawlSummary> CrawlAll(FakeHandler handler, FakeClock clock, CrawlRunOptions? run = null,
        CancellationToken cancellationToken = default, CrawlerOptions? options = null, TextWriter? log = null)
    {
        options ??= new CrawlerOptions();
        log ??= TextWriter.Null;
        using var client = new PoliteHttpClient(handler, options, clock, log);
        var crawler = new MultiTaxCrawler(client, [_store, CivaStore], new PdfTextExtractor(), options, _ => log);
        return await crawler.RunAsync(run ?? new CrawlRunOptions(), cancellationToken);
    }

    [Fact]
    public async Task AllCrawlsEveryTaxInOrderThroughOnePoliteClient()
    {
        var clock = new FakeClock();
        var handler = TwoTaxSite(clock);
        var options = new CrawlerOptions { RequestInterval = TimeSpan.FromSeconds(2) };

        var summary = await CrawlAll(handler, clock, options: options);

        Assert.True(summary.IsComplete);
        Assert.Equal(
            [
                TestListing.RobotsUrl, ListingUrl, TestListing.PdfUrl("PIV_1.pdf"), TestListing.PdfUrl("PIV_2.pdf"),
                TestListing.RobotsUrl, CivaListingUrl, CivaPdf("PIV_1.pdf"), CivaPdf("PIV_95001.pdf"), CivaPdf("PIV_95003.pdf"),
            ],
            handler.Requests.Select(r => r.Url.AbsoluteUri));
        // One request at a time, the full interval apart, also from the last CIRS PDF to the CIVA robots.txt.
        for (var i = 1; i < handler.Requests.Count; i++)
        {
            Assert.True(handler.Requests[i].Start - handler.Requests[i - 1].Start >= options.RequestInterval + handler.TransferTime,
                $"request {i} started too early");
        }
        Assert.All(handler.Requests, r => Assert.StartsWith("LupaFiscal/0.2 (+https://github.com/", r.UserAgent));
        Assert.Equal([4, 5], summary.Taxes.Select(t => t.Summary!.Requests));
        Assert.Equal(9, summary.Requests);
        Assert.Equal(5, summary.Downloads);

        var cirs = _store.LoadManifest()!.Rulings.ToDictionary(r => r.Id);
        var civa = CivaStore.LoadManifest()!.Rulings.ToDictionary(r => r.Id);
        Assert.Equal(["piv_1", "piv_2"], cirs.Keys.Order());
        Assert.Equal(["civa-piv_1", "civa-piv_95001", "civa-piv_95003"], civa.Keys.Order());
        Assert.All(civa.Values, r => Assert.Equal("CIVA", r.Tax));
        Assert.Equal(Path.Combine(_temp.Path, "civa"), CivaStore.TaxDirectory);

        // Byte-identical PDFs share their hash; the same file name with other content does not.
        Assert.Equal(cirs["piv_2"].PdfSha256, civa["civa-piv_95001"].PdfSha256);
        Assert.NotEqual(cirs["piv_1"].PdfSha256, civa["civa-piv_1"].PdfSha256);
        Assert.Contains(TestPdfs.Expect("armazém"), File.ReadAllText(CivaStore.TextPath("civa-piv_1")), StringComparison.Ordinal);
        Assert.DoesNotContain(TestPdfs.Expect("armazém"), File.ReadAllText(_store.TextPath("piv_1")), StringComparison.Ordinal);

        // The scanned PDF is listed in its own tax's report and has no text.
        Assert.Equal(new CorpusCounts(3, 0, 0, 2, 1, 0, 0), summary.Taxes[1].Counts);
        Assert.Equal(RulingState.ScannedSkipped, civa["civa-piv_95003"].State);
        Assert.False(File.Exists(CivaStore.TextPath("civa-piv_95003")));
        Assert.Contains("| civa-piv_95003 |", File.ReadAllText(CivaStore.ScannedReportPath), StringComparison.Ordinal);
        Assert.Contains("Count: 0", File.ReadAllText(_store.ScannedReportPath), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RobotsTxtIsFetchedAgainAtTheStartOfEachTax()
    {
        var clock = new FakeClock();
        var handler = TwoTaxSite(clock);
        handler.Then(TestListing.RobotsUrl, () => FakeHandler.Status(HttpStatusCode.NotFound));
        handler.Then(TestListing.RobotsUrl, () => FakeHandler.Text($"User-agent: *\nDisallow: {TestListing.DocumentsPathFor(Civa)}"));

        var summary = await CrawlAll(handler, clock);

        Assert.Equal(2, handler.CountFor(TestListing.RobotsUrl));
        Assert.Equal(2, summary.Taxes[0].Counts!.Extracted);
        Assert.Equal(3, summary.Taxes[1].Counts!.Failed);
        Assert.Equal(0, handler.Requests.Count(r => r.Url.AbsoluteUri.StartsWith(CivaPdf(""), StringComparison.Ordinal)));
        Assert.All(CivaStore.LoadManifest()!.Rulings, r => Assert.Contains("robots.txt", r.Reason, StringComparison.Ordinal));
    }

    [Fact]
    public async Task MaxDownloadsAppliesAcrossTheWholeAllRun()
    {
        var clock = new FakeClock();
        var first = TwoTaxSite(clock);
        var summary = await CrawlAll(first, clock, new CrawlRunOptions { MaxDownloads = 3 });

        Assert.Equal(3, PdfRequests(first));
        Assert.Equal(3, summary.Downloads);
        Assert.Equal(2, summary.Taxes[0].Counts!.Extracted);
        Assert.Equal(2, summary.Taxes[1].Counts!.Pending);
        Assert.False(summary.IsComplete);

        var second = TwoTaxSite(clock);
        summary = await CrawlAll(second, clock, new CrawlRunOptions { MaxDownloads = 5 });

        Assert.Equal(2, PdfRequests(second));
        Assert.True(summary.IsComplete);
    }

    [Fact]
    public async Task TaxesAfterTheDownloadBudgetIsUsedAreNotRequested()
    {
        var clock = new FakeClock();
        var handler = TwoTaxSite(clock);

        var summary = await CrawlAll(handler, clock, new CrawlRunOptions { MaxDownloads = 2 });

        Assert.Equal(0, handler.CountFor(CivaListingUrl));
        Assert.Equal(1, handler.CountFor(TestListing.RobotsUrl));
        Assert.True(summary.Taxes[0].IsComplete);
        Assert.Null(summary.Taxes[1].Summary);
        Assert.Null(summary.Taxes[1].Counts);
        Assert.Contains("budget", summary.Taxes[1].Skipped, StringComparison.Ordinal);
        Assert.False(summary.IsComplete);
        Assert.False(summary.Aborted);
    }

    [Fact]
    public async Task AllRunResumesAfterAnInterruptionInTheSecondTax()
    {
        var clock = new FakeClock();
        var first = TwoTaxSite(clock);
        using var interruption = new CancellationTokenSource();
        first.OnRequest = url =>
        {
            if (url.AbsoluteUri == CivaPdf("PIV_95001.pdf")) interruption.Cancel();
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CrawlAll(first, clock, cancellationToken: interruption.Token));

        Assert.True(CorpusCounts.From(_store.LoadManifest()!).IsComplete);
        var civa = CorpusCounts.From(CivaStore.LoadManifest()!);
        Assert.Equal(1, civa.Extracted);
        Assert.Equal(2, civa.Pending);

        var second = TwoTaxSite(clock);
        var summary = await CrawlAll(second, clock);

        Assert.True(summary.IsComplete);
        Assert.Equal(0, second.CountFor(TestListing.PdfUrl("PIV_1.pdf")));
        Assert.Equal(0, second.CountFor(TestListing.PdfUrl("PIV_2.pdf")));
        Assert.Equal(0, second.CountFor(CivaPdf("PIV_1.pdf")));
        Assert.Equal(2, PdfRequests(second));
    }

    [Fact]
    public async Task ThrottledDownloadInTheSecondTaxBacksOffAndHonoursRetryAfter()
    {
        var clock = new FakeClock();
        var handler = TwoTaxSite(clock);
        handler.Then(CivaPdf("PIV_1.pdf"), () => FakeHandler.Status(HttpStatusCode.TooManyRequests, TimeSpan.FromSeconds(30)));
        handler.Then(CivaPdf("PIV_1.pdf"), () => FakeHandler.Status(HttpStatusCode.ServiceUnavailable));
        var options = new CrawlerOptions { MaxRetries = 2, InitialBackoff = TimeSpan.FromSeconds(5) };

        var summary = await CrawlAll(handler, clock, options: options);

        Assert.True(summary.IsComplete);
        Assert.Equal(3, handler.CountFor(CivaPdf("PIV_1.pdf")));
        Assert.Contains(TimeSpan.FromSeconds(30), clock.Delays);
        Assert.Contains(TimeSpan.FromSeconds(10), clock.Delays);
        Assert.Equal(RulingState.Extracted, CivaStore.LoadManifest()!.Rulings.Single(r => r.Id == "civa-piv_1").State);
    }

    [Fact]
    public async Task RepeatedFailuresStopTheAllRunAndLeaveRulingsPending()
    {
        var clock = new FakeClock();
        var handler = TwoTaxSite(clock);
        handler.Always(ListingUrl, () => FakeHandler.Text(TestListing.ForNumbers(1, 2, 3, 4), "application/json"));
        foreach (var number in new[] { 1, 2, 3, 4 })
        {
            handler.Always(TestListing.PdfUrl($"PIV_{number}.pdf"), () => FakeHandler.Status(HttpStatusCode.BadGateway));
        }

        var summary = await CrawlAll(handler, clock, options: new CrawlerOptions { MaxRetries = 1 });

        Assert.True(summary.Aborted);
        Assert.StartsWith("CIRS: the site looks unavailable", summary.AbortReason, StringComparison.Ordinal);
        Assert.Equal(4, summary.Taxes[0].Counts!.Pending);
        Assert.Equal(0, handler.CountFor(CivaListingUrl));
        Assert.Equal(1, handler.CountFor(TestListing.RobotsUrl));
        Assert.Contains("earlier tax", summary.Taxes[1].Skipped, StringComparison.Ordinal);
        Assert.False(summary.IsComplete);
    }

    [Fact]
    public async Task ListingLinksLeavingTheHostUsingHttpOrNotPdfsAreRejectedLoggedAndNeverRequested()
    {
        var clock = new FakeClock();
        var handler = TwoTaxSite(clock);
        handler.Always(CivaListingUrl, () => FakeHandler.Text(TestListing.ReadFixture("listing-civa.json"), "application/json"));
        var log = new StringWriter();

        var summary = await CrawlAll(handler, clock, log: log);

        Assert.Equal(4, summary.Taxes[1].Counts!.Listed);
        var output = log.ToString();
        Assert.Contains("Listing entry skipped: row 5: PDF link uses http, not https", output, StringComparison.Ordinal);
        Assert.Contains("Listing entry skipped: row 6: PDF link leaves the allowlisted host", output, StringComparison.Ordinal);
        Assert.Contains("Listing entry skipped: row 7: link is not a PDF", output, StringComparison.Ordinal);
        Assert.All(handler.Requests, r =>
        {
            Assert.Equal("https", r.Url.Scheme);
            Assert.Equal("info.portaldasfinancas.gov.pt", r.Url.Host);
            Assert.DoesNotContain(".docx", r.Url.AbsolutePath, StringComparison.Ordinal);
        });
    }
}
