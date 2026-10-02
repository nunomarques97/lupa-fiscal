using LupaFiscal.Core.Crawling;
using LupaFiscal.Tests.Support;

namespace LupaFiscal.Tests;

public class ListingParserTests
{
    private static readonly UrlPolicy Policy = new(["info.portaldasfinancas.gov.pt"]);

    private static ListingParseResult ParseFixture() =>
        ListingParser.Parse(TestListing.ReadFixture("listing-cirs.json"), TaxSource.Cirs, Policy);

    [Fact]
    public void ParsesEveryFieldOfAStandardRow()
    {
        var entry = ParseFixture().Entries.Single(e => e.Id == "piv_31329");

        Assert.Equal("PIV_31329.pdf", entry.FileName);
        Assert.Equal("https://info.portaldasfinancas.gov.pt/pt/informacao_fiscal/informacoes_vinculativas/rendimento/cirs/Documents/PIV_31329.pdf", entry.SourceUrl);
        Assert.Equal("CIRS", entry.Tax);
        Assert.Equal("CIRS", entry.Diploma);
        Assert.Equal("10", entry.Article);
        Assert.Equal("n.º 07", entry.Paragraph);
        Assert.Equal(new DateOnly(2026, 9, 23), entry.PublishedOn);
        Assert.Equal("31329", entry.ProcessNumber);
        Assert.StartsWith("Reinvestimento do valor de realização de HPP", entry.Subject);
    }

    [Fact]
    public void FileNameNotNumberIsTheIdentity()
    {
        var entry = ParseFixture().Entries.Single(e => e.ProcessNumber == "29710");

        Assert.Equal("piv_30351", entry.Id);
        Assert.Equal("12-B", entry.Article);
        Assert.Equal("n.º1, al. d)", entry.Paragraph);
    }

    [Fact]
    public void FreeFormFileNamesAreSanitisedAndUrlEncoded()
    {
        var entry = ParseFixture().Entries.Single(e => e.FileName.StartsWith("Ficha", StringComparison.Ordinal));

        Assert.Equal("ficha-doutrinaria-proc-2006-000123_pensoesdef_farmadas", entry.Id);
        Assert.Equal(
            "https://info.portaldasfinancas.gov.pt/pt/informacao_fiscal/informacoes_vinculativas/rendimento/cirs/Documents/Ficha%20Doutrin%C3%A1ria%20-%20Proc%202006%20000123_PensoesDef_FArmadas.pdf",
            entry.SourceUrl);
        Assert.Equal("", entry.ProcessNumber);
        Assert.Equal("236", entry.Article);
        Assert.Equal("", entry.Paragraph);
        Assert.Equal(new DateOnly(2006, 8, 24), entry.PublishedOn);
    }

    [Fact]
    public void KeepsTheDiplomaAndDecodesEntities()
    {
        var entry = ParseFixture().Entries.Single(e => e.Id == "piv_28001");

        Assert.Equal("CIRS", entry.Tax);
        Assert.Equal("Estatuto dos Benefícios Fiscais (EBF)", entry.Diploma);
        Assert.Equal("16", entry.Article);
        Assert.Equal("n.º 05, al. e)", entry.Paragraph);
        Assert.Equal("Benefícios fiscais - Contas poupança & reformados", entry.Subject);
    }

    [Fact]
    public void RejectsOtherHostsPlainHttpAndMalformedRows()
    {
        var result = ParseFixture();

        Assert.Equal(5, result.Entries.Count);
        Assert.Equal(4, result.Rejected.Count);
        Assert.DoesNotContain(result.Entries, e => e.SourceUrl.Contains("evil", StringComparison.Ordinal));
        Assert.All(result.Entries, e => Assert.StartsWith("https://info.portaldasfinancas.gov.pt/", e.SourceUrl));
        Assert.Contains(result.Rejected, r => r.Contains("allowlist", StringComparison.Ordinal));
        Assert.Contains(result.Rejected, r => r.Contains("shape", StringComparison.Ordinal));
        Assert.Contains(result.Rejected, r => r.Contains("no PDF link", StringComparison.Ordinal));
    }

    [Fact]
    public void ProtocolRelativeLinkToAnotherHostIsRejected()
    {
        var json = TestListing.Json(TestListing.Row("//evil.example/a.pdf", "1"));

        var result = ListingParser.Parse(json, TaxSource.Cirs, Policy);

        Assert.Empty(result.Entries);
        Assert.Single(result.Rejected);
    }

    [Fact]
    public void IdsThatSanitiseToTheSameValueGetDistinctSuffixes()
    {
        var json = TestListing.Json(
            TestListing.Row(TestListing.DocumentsPath + "PIV_1.pdf", "1"),
            TestListing.Row(TestListing.DocumentsPath + "piv 1.pdf", "1"),
            TestListing.Row(TestListing.DocumentsPath + "PIV_2.pdf", "2"),
            TestListing.Row(TestListing.DocumentsPath + "PIV_2.pdf", "2"));

        var result = ListingParser.Parse(json, TaxSource.Cirs, Policy);

        Assert.Equal(3, result.Entries.Count);
        Assert.Equal(3, result.Entries.Select(e => e.Id).Distinct().Count());
        Assert.Contains(result.Entries, e => e.Id == "piv_2");
        Assert.All(result.Entries, e => Assert.True(RulingId.IsValid(e.Id)));
    }

    [Fact]
    public void MissingDataArrayIsAFormatError()
    {
        Assert.Throws<FormatException>(() => ListingParser.Parse("{\"total\":0}", TaxSource.Cirs, Policy));
    }

    [Theory]
    [InlineData("010", "10")]
    [InlineData("012-B", "12-B")]
    [InlineData("0236", "236")]
    [InlineData("10", "10")]
    [InlineData("000", "0")]
    [InlineData("", "")]
    [InlineData("Art. único", "Art. único")]
    public void NormalisesArticles(string raw, string expected)
    {
        Assert.Equal(expected, ListingParser.NormalizeArticle(raw));
    }

    [Fact]
    public void ListingUriMatchesTheGateZeroEndpoint()
    {
        Assert.Equal(
            "https://info.portaldasfinancas.gov.pt/pt/informacao_fiscal/informacoes_vinculativas/rendimento/cirs/_vti_bin/portalat/docs.svc/listdocs"
            + "?fields=DocIcon,NumeroVinculativa,Disponibilizada_x0020_em,Diploma,Artigo,N_x002e__x00ba__x002f_Al_x00ed_nea,Assunto"
            + "&sort=NumeroVinculativa:DESC&filter=%3CIsNotNull%3E%3CFieldRef%20Name%3D%22ID%22%3E%3C%2FFieldRef%3E%3C%2FIsNotNull%3E&id=42",
            TaxSource.Cirs.ListingUri.AbsoluteUri);
    }
}
