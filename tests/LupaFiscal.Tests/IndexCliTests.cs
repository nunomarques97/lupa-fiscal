using LupaFiscal.Cli;
using LupaFiscal.Tests.Support;

namespace LupaFiscal.Tests;

public sealed class IndexCliTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private async Task<(int Exit, string Out, string Err)> Run(params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        // The fake embedder stands in for the model, so nothing is downloaded or loaded.
        var exit = await CliApp.RunAsync([.. args, "--data-dir", _temp.Path], stdout, stderr, CancellationToken.None,
            () => throw new InvalidOperationException("no network in tests"), () => new FakeEmbedder());
        return (exit, stdout.ToString(), stderr.ToString());
    }

    [Fact]
    public async Task IndexThenStatusThenSearch()
    {
        TestCorpus.Write(_temp.Path, TestCorpus.All);

        var index = await Run("index");
        var status = await Run("index-status");
        var search = await Run("search", "Posso", "deduzir", "despesas de educação dos filhos?", "--limit", "3");

        Assert.Equal(CliApp.ExitOk, index.Exit);
        Assert.Equal(CliApp.ExitOk, status.Exit);
        Assert.Contains("CIRS extracted rulings: 4", status.Out, StringComparison.Ordinal);
        Assert.Contains("missing (no chunks):    0", status.Out, StringComparison.Ordinal);
        Assert.Contains("Status: complete", status.Out, StringComparison.Ordinal);

        Assert.Equal(CliApp.ExitOk, search.Exit);
        Assert.Contains("Query: Posso deduzir despesas de educação dos filhos?", search.Out, StringComparison.Ordinal);
        Assert.Contains("1. [", search.Out, StringComparison.Ordinal);
        Assert.Contains("CIRS art. 78-D | processo 90001 | 2024-03-12", search.Out, StringComparison.Ordinal);
        Assert.Contains("https://info.portaldasfinancas.gov.pt/", search.Out, StringComparison.Ordinal);
        Assert.Contains("[despesas]", search.Out, StringComparison.Ordinal);
        Assert.Matches(@"Search took \d+ ms \(3 result\(s\)\)", search.Out);
    }

    [Fact]
    public async Task ReindexingDoesNotDuplicateAndStatusFailsUntilANewRulingIsIndexed()
    {
        TestCorpus.Write(_temp.Path, [TestCorpus.Education, TestCorpus.Rental]);
        await Run("index");
        var first = await Run("index-status");

        var second = await Run("index");
        var again = await Run("index-status");

        Assert.Contains("embedded 0 (0 chunk(s)), unchanged 2", second.Out, StringComparison.Ordinal);
        Assert.Equal(ChunkLine(first.Out), ChunkLine(again.Out));

        TestCorpus.Write(_temp.Path, TestCorpus.All);
        var stale = await Run("index-status");
        Assert.Equal(CliApp.ExitFailure, stale.Exit);
        Assert.Contains("missing (no chunks):    2", stale.Out, StringComparison.Ordinal);

        var added = await Run("index");
        Assert.Equal(CliApp.ExitOk, added.Exit);
        Assert.Contains("embedded 2", added.Out, StringComparison.Ordinal);
        Assert.Equal(CliApp.ExitOk, (await Run("index-status")).Exit);
    }

    private static string ChunkLine(string output) =>
        output.Split('\n').Select(l => l.Trim()).Single(l => l.StartsWith("chunks:", StringComparison.Ordinal));

    [Fact]
    public async Task StatusFailsWithoutAnIndex()
    {
        TestCorpus.Write(_temp.Path, TestCorpus.All);

        var (exit, _, err) = await Run("index-status");

        Assert.Equal(CliApp.ExitFailure, exit);
        Assert.Contains("No index", err, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("search")]
    [InlineData("search", "   ")]
    [InlineData("search", "IRS", "--limit", "0")]
    [InlineData("search", "IRS", "--limit", "51")]
    [InlineData("search", "IRS", "--year", "dois mil")]
    [InlineData("search", "IRS", "--mode", "fuzzy")]
    [InlineData("search", "IRS", "--query", "IRS")]
    [InlineData("model", "upload")]
    [InlineData("index", "extra")]
    public async Task RejectsInvalidArguments(params string[] args)
    {
        TestCorpus.Write(_temp.Path, TestCorpus.All);
        await Run("index");

        var (exit, _, _) = await Run(args);

        Assert.Equal(CliApp.ExitUsage, exit);
    }

    [Fact]
    public async Task SearchWithFtsSyntaxAndFiltersNeverFails()
    {
        TestCorpus.Write(_temp.Path, TestCorpus.All);
        await Run("index");

        var syntax = await Run("search", "\"NEAR(despesas* AND -educação\" OR text:(", "--mode", "keyword");
        var filtered = await Run("search", "rendimentos", "--tax", "CIRS", "--article", "8", "--year", "2022");
        var empty = await Run("search", "rendimentos", "--article", "999");

        Assert.Equal(CliApp.ExitOk, syntax.Exit);
        Assert.Equal(CliApp.ExitOk, filtered.Exit);
        Assert.Contains("processo 90003", filtered.Out, StringComparison.Ordinal);
        Assert.DoesNotContain("processo 90001", filtered.Out, StringComparison.Ordinal);
        Assert.Equal(CliApp.ExitOk, empty.Exit);
        Assert.Contains("No results.", empty.Out, StringComparison.Ordinal);
    }
}
