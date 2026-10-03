using System.Net;
using LupaFiscal.Cli;
using LupaFiscal.Core.Corpus;
using LupaFiscal.Core.Crawling;
using LupaFiscal.Tests.Support;

namespace LupaFiscal.Tests;

public sealed class CliTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private async Task<(int Exit, string Out, string Err)> Run(params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exit = await CliApp.RunAsync(args, stdout, stderr, CancellationToken.None, () => new FakeHandler(new FakeClock()));
        return (exit, stdout.ToString(), stderr.ToString());
    }

    private void WriteManifest(params RulingState[] states)
    {
        var store = new CorpusStore(Path.Combine(_temp.Path, "corpus"), TaxSource.Cirs);
        var manifest = new CorpusManifest { Tax = "CIRS" };
        for (var i = 0; i < states.Length; i++)
        {
            manifest.Rulings.Add(new RulingRecord
            {
                Id = $"piv_{i}",
                FileName = $"PIV_{i}.pdf",
                SourceUrl = TestListing.PdfUrl($"PIV_{i}.pdf"),
                Tax = "CIRS",
                State = states[i],
            });
        }
        store.SaveManifest(manifest);
    }

    [Fact]
    public async Task StatusFailsWithoutACorpus()
    {
        var (exit, _, err) = await Run("corpus-status", "--tax", "CIRS", "--data-dir", _temp.Path);

        Assert.Equal(CliApp.ExitFailure, exit);
        Assert.Contains("No corpus", err, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StatusFailsWhileAListedRulingIsPending()
    {
        WriteManifest(RulingState.Extracted, RulingState.Pending);

        var (exit, output, _) = await Run("corpus-status", "--tax", "CIRS", "--data-dir", _temp.Path);

        Assert.Equal(CliApp.ExitFailure, exit);
        Assert.Contains("pending:         1", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StatusFailsWhileADownloadAwaitsExtraction()
    {
        WriteManifest(RulingState.Extracted, RulingState.Downloaded);

        var (exit, _, _) = await Run("corpus-status", "--tax", "CIRS", "--data-dir", _temp.Path);

        Assert.Equal(CliApp.ExitFailure, exit);
    }

    [Fact]
    public async Task StatusSucceedsWhenNothingIsPendingAndPrintsEveryCount()
    {
        WriteManifest(RulingState.Extracted, RulingState.Extracted, RulingState.ScannedSkipped, RulingState.Failed);

        var (exit, output, _) = await Run("corpus-status", "--tax", "cirs", "--data-dir", _temp.Path, "--list-failed");

        Assert.Equal(CliApp.ExitOk, exit);
        Assert.Contains("CIRS listed: 4", output, StringComparison.Ordinal);
        Assert.Contains("downloaded:      3", output, StringComparison.Ordinal);
        Assert.Contains("extracted:       2", output, StringComparison.Ordinal);
        Assert.Contains("scanned-skipped: 1", output, StringComparison.Ordinal);
        Assert.Contains("failed:          1", output, StringComparison.Ordinal);
        Assert.Contains("failed piv_3", output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("corpus-status")]
    [InlineData("corpus-status", "--tax", "IVA")]
    [InlineData("corpus-status", "--tax", "../x")]
    [InlineData("corpus-status", "--tax", "CIRS", "--bogus", "1")]
    [InlineData("crawl", "--tax", "CIRS", "--interval-seconds", "0.5")]
    [InlineData("crawl", "--tax", "CIRS", "--interval-seconds", "NaN")]
    [InlineData("crawl", "--tax", "CIRS", "--max-retries", "99")]
    [InlineData("unknown-command")]
    public async Task InvalidUsageExitsWithUsageError(params string[] args)
    {
        var (exit, _, _) = await Run([.. args, "--data-dir", _temp.Path]);

        Assert.Equal(CliApp.ExitUsage, exit);
    }

    [Fact]
    public async Task CrawlRunsEndToEndAgainstAFakeSite()
    {
        var clock = new FakeClock();
        var handler = new FakeHandler(clock);
        handler.Always(TestListing.RobotsUrl, () => FakeHandler.Status(HttpStatusCode.NotFound));
        handler.Always(TaxSource.Cirs.ListingUri.AbsoluteUri, () => FakeHandler.Text(TestListing.ForNumbers(7), "application/json"));
        handler.Always(TestListing.PdfUrl("PIV_7.pdf"), () => FakeHandler.Bytes(TestPdfs.Ruling()));
        var stdout = new StringWriter();

        var exit = await CliApp.RunAsync(["crawl", "--tax", "CIRS", "--data-dir", _temp.Path], stdout, TextWriter.Null,
            CancellationToken.None, () => handler);

        Assert.Equal(CliApp.ExitOk, exit);
        Assert.Equal(3, handler.Requests.Count);
        Assert.True(File.Exists(Path.Combine(_temp.Path, "corpus", "cirs", "text", "piv_7.txt")));
        Assert.True(File.Exists(Path.Combine(_temp.Path, "corpus", "cirs", "crawl.log")));
        var (statusExit, _, _) = await Run("corpus-status", "--tax", "CIRS", "--data-dir", _temp.Path);
        Assert.Equal(CliApp.ExitOk, statusExit);

        var requestsBefore = handler.Requests.Count;
        var (extractExit, extractOut, _) = await Run("extract", "--tax", "CIRS", "--data-dir", _temp.Path);
        Assert.Equal(CliApp.ExitOk, extractExit);
        Assert.Contains("Extracted 1 cached PDF(s) again.", extractOut, StringComparison.Ordinal);
        Assert.Equal(requestsBefore, handler.Requests.Count);
    }

    private void WriteManifest(TaxSource source, params RulingState[] states)
    {
        var store = new CorpusStore(Path.Combine(_temp.Path, "corpus"), source);
        var manifest = new CorpusManifest { Tax = source.Code };
        for (var i = 0; i < states.Length; i++)
        {
            manifest.Rulings.Add(new RulingRecord
            {
                Id = $"{source.IdPrefix}piv_{i}",
                FileName = $"PIV_{i}.pdf",
                SourceUrl = TestListing.PdfUrlFor(source, $"PIV_{i}.pdf"),
                Tax = source.Code,
                State = states[i],
                Reason = states[i] == RulingState.Failed ? "HTTP 404" : null,
            });
        }
        store.SaveManifest(manifest);
    }

    [Fact]
    public async Task StatusAllFailsWithoutAnyCorpus()
    {
        var (exit, output, _) = await Run("corpus-status", "--all", "--data-dir", _temp.Path);

        Assert.Equal(CliApp.ExitFailure, exit);
        Assert.Contains("No corpus for CIRS", output, StringComparison.Ordinal);
        Assert.Contains("All 13 taxes (0 crawled) listed: 0", output, StringComparison.Ordinal);
        Assert.Contains("Status: incomplete (CIRS, CIRC,", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StatusAllSucceedsOnlyWhenEveryTaxIsCompleteAndPrintsCountsPerTax()
    {
        foreach (var source in TaxSource.All)
        {
            WriteManifest(source, RulingState.Extracted, RulingState.ScannedSkipped, RulingState.Failed);
        }

        var (exit, output, _) = await Run("corpus-status", "--all", "--data-dir", _temp.Path, "--list-failed");

        Assert.Equal(CliApp.ExitOk, exit);
        foreach (var source in TaxSource.All)
        {
            Assert.Contains($"Corpus {source.Code} (", output, StringComparison.Ordinal);
            Assert.Contains($"{source.Code} listed: 3", output, StringComparison.Ordinal);
            Assert.Contains($"failed {source.IdPrefix}piv_2: HTTP 404", output, StringComparison.Ordinal);
        }
        Assert.Contains("All 13 taxes (13 crawled) listed: 39", output, StringComparison.Ordinal);
        Assert.Contains("extracted:       13", output, StringComparison.Ordinal);
        Assert.Contains("scanned-skipped: 13", output, StringComparison.Ordinal);
        Assert.Contains("failed:          13", output, StringComparison.Ordinal);
        Assert.Contains("Status: complete (no listed ruling of any supported tax is pending).", output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(RulingState.Pending)]
    [InlineData(RulingState.Downloaded)]
    [InlineData(null)]
    public async Task StatusAllFailsWhenOneTaxIsPendingOrMissing(RulingState? civaState)
    {
        foreach (var source in TaxSource.All.Where(s => s.Code != "CIVA"))
        {
            WriteManifest(source, RulingState.Extracted);
        }
        if (civaState is { } state) WriteManifest(TaxSource.Find("CIVA")!, RulingState.Extracted, state);

        var (exit, output, _) = await Run("corpus-status", "--all", "--data-dir", _temp.Path);

        Assert.Equal(CliApp.ExitFailure, exit);
        Assert.Contains("Status: incomplete (CIVA). Run: crawl --all", output, StringComparison.Ordinal);
        var (cirsExit, _, _) = await Run("corpus-status", "--tax", "CIRS", "--data-dir", _temp.Path);
        Assert.Equal(CliApp.ExitOk, cirsExit);
    }

    [Theory]
    [InlineData("corpus-status", "--all", "--tax", "CIRS")]
    [InlineData("crawl", "--tax", "CIVA", "--all")]
    [InlineData("extract", "--all", "--tax", "CIVA")]
    [InlineData("crawl", "--all", "--max-downloads", "-1")]
    [InlineData("extract", "--all")]
    public async Task AllAndTaxTogetherOrWithoutACorpusAreUsageErrors(params string[] args)
    {
        var (exit, _, err) = await Run([.. args, "--data-dir", _temp.Path]);

        Assert.Equal(CliApp.ExitUsage, exit);
        Assert.NotEmpty(err);
    }

    [Fact]
    public async Task CrawlAllExtractAllAndStatusAllRunEndToEndAgainstAFakeSite()
    {
        var clock = new FakeClock();
        var handler = new FakeHandler(clock);
        handler.Always(TestListing.RobotsUrl, () => FakeHandler.Status(HttpStatusCode.NotFound));
        foreach (var source in TaxSource.All)
        {
            handler.Always(source.ListingUri.AbsoluteUri, () => FakeHandler.Text(TestListing.ForNumbers(source, 7), "application/json"));
            handler.Always(TestListing.PdfUrlFor(source, "PIV_7.pdf"), () => FakeHandler.Bytes(TestPdfs.Ruling()));
        }
        var stdout = new StringWriter();

        var exit = await CliApp.RunAsync(["crawl", "--all", "--data-dir", _temp.Path], stdout, TextWriter.Null,
            CancellationToken.None, () => handler, crawlClock: clock);

        Assert.Equal(CliApp.ExitOk, exit);
        Assert.Equal(3 * TaxSource.All.Count, handler.Requests.Count);
        Assert.Equal(TaxSource.All.Count, handler.CountFor(TestListing.RobotsUrl));
        Assert.Equal(
            TaxSource.All.Select(s => s.ListingUri.AbsoluteUri),
            handler.Requests.Where(r => r.Url.AbsolutePath.EndsWith("/listdocs", StringComparison.Ordinal)).Select(r => r.Url.AbsoluteUri));
        foreach (var source in TaxSource.All)
        {
            var directory = Path.Combine(_temp.Path, "corpus", source.DirectoryName);
            Assert.True(File.Exists(Path.Combine(directory, "text", source.IdPrefix + "piv_7.txt")));
            var log = File.ReadAllText(Path.Combine(directory, "crawl.log"));
            Assert.Contains($"GET {TestListing.PdfUrlFor(source, "PIV_7.pdf")} -> 200", log, StringComparison.Ordinal);
            Assert.Contains($"{source.Code} listed: 1", log, StringComparison.Ordinal);
        }
        // Each tax's requests are logged in its own crawl.log only.
        Assert.DoesNotContain("/despesa/civa/", File.ReadAllText(Path.Combine(_temp.Path, "corpus", "cirs", "crawl.log")),
            StringComparison.Ordinal);
        Assert.Contains("Crawl finished: 39 request(s), 13 PDF download(s).", stdout.ToString(), StringComparison.Ordinal);

        var requestsBefore = handler.Requests.Count;
        var (extractExit, extractOut, _) = await Run("extract", "--all", "--data-dir", _temp.Path);
        Assert.Equal(CliApp.ExitOk, extractExit);
        Assert.Equal(TaxSource.All.Count, extractOut.Split("Extracted 1 cached PDF(s) again.").Length - 1);
        Assert.Equal(requestsBefore, handler.Requests.Count);

        var (statusExit, _, _) = await Run("corpus-status", "--all", "--data-dir", _temp.Path);
        Assert.Equal(CliApp.ExitOk, statusExit);
    }

    [Fact]
    public async Task TwoTaxFixtureCorpusHasACopyASameNameRulingAndAScannedPdf()
    {
        var stores = TestCorpus.WriteTaxes(_temp.Path, TestCorpus.TwoTaxes);

        Assert.Equal(["CIRS", "CIVA"], stores.Select(s => s.Source.Code));
        var cirs = stores[0].LoadManifest()!.Rulings.ToDictionary(r => r.Id);
        var civa = stores[1].LoadManifest()!.Rulings.ToDictionary(r => r.Id);
        Assert.All(civa.Keys, id => Assert.True(RulingId.IsValid(id) && !cirs.ContainsKey(id)));
        Assert.Equal(cirs["piv_90001"].PdfSha256, civa["civa-piv_95001"].PdfSha256);
        Assert.Equal(cirs["piv_90002"].FileName, civa["civa-piv_90002"].FileName);
        Assert.NotEqual(cirs["piv_90002"].PdfSha256, civa["civa-piv_90002"].PdfSha256);
        Assert.Contains("/despesa/civa/Documents/PIV_90002.pdf", civa["civa-piv_90002"].SourceUrl, StringComparison.Ordinal);
        Assert.Equal(cirs["piv_90003"].Article, civa["civa-piv_95002"].Article);
        Assert.Equal(RulingState.ScannedSkipped, civa["civa-piv_95003"].State);
        Assert.False(File.Exists(stores[1].TextPath("civa-piv_95003")));
        Assert.Contains("| civa-piv_95003 |", File.ReadAllText(stores[1].ScannedReportPath), StringComparison.Ordinal);

        var (civaExit, output, _) = await Run("corpus-status", "--tax", "CIVA", "--data-dir", _temp.Path);
        Assert.Equal(CliApp.ExitOk, civaExit);
        Assert.Contains("scanned-skipped: 1", output, StringComparison.Ordinal);
        var (allExit, _, _) = await Run("corpus-status", "--all", "--data-dir", _temp.Path);
        Assert.Equal(CliApp.ExitFailure, allExit);
    }

    [Fact]
    public async Task ExtractAllSkipsTaxesWithoutACorpusAndFails()
    {
        WriteManifest(TaxSource.Cirs, RulingState.Extracted);

        var (exit, output, _) = await Run("extract", "--all", "--data-dir", _temp.Path);

        Assert.Equal(CliApp.ExitFailure, exit);
        Assert.Contains("No corpus for CIVA", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExtractWithoutACorpusIsAUsageError()
    {
        var (exit, _, err) = await Run("extract", "--tax", "CIRS", "--data-dir", _temp.Path);

        Assert.Equal(CliApp.ExitUsage, exit);
        Assert.Contains("No corpus", err, StringComparison.Ordinal);
    }
}
