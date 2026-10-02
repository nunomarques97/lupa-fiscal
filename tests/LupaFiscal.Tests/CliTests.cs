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

    [Fact]
    public async Task ExtractWithoutACorpusIsAUsageError()
    {
        var (exit, _, err) = await Run("extract", "--tax", "CIRS", "--data-dir", _temp.Path);

        Assert.Equal(CliApp.ExitUsage, exit);
        Assert.Contains("No corpus", err, StringComparison.Ordinal);
    }
}
