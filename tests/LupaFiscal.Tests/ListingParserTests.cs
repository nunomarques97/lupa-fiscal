using LupaFiscal.Core.Crawling;
using LupaFiscal.Tests.Support;

namespace LupaFiscal.Tests;

public class ListingParserTests
{
    private static readonly UrlPolicy Policy = new(["info.portaldasfinancas.gov.pt"]);

    private static readonly TaxSource Civa = TaxSource.Find("CIVA")!;

    private const string RulingsRoot = "/pt/informacao_fiscal/informacoes_vinculativas";

    private const string UnfilteredFilter = "%3CIsNotNull%3E%3CFieldRef%20Name%3D%22ID%22%3E%3C%2FFieldRef%3E%3C%2FIsNotNull%3E";

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
    public void ParsesTheCivaListingShapeByFieldName()
    {
        var result = ListingParser.Parse(TestListing.ReadFixture("listing-civa.json"), Civa, Policy);

        Assert.Equal(4, result.Entries.Count);
        var exemption = result.Entries.Single(e => e.ProcessNumber == "30858");
        Assert.Equal("civa-piv_30858", exemption.Id);
        Assert.Equal("CIVA", exemption.Tax);
        Assert.Equal("CIVA", exemption.Diploma);
        Assert.Equal("9", exemption.Article);
        Assert.Equal("n.º 29", exemption.Paragraph);
        Assert.Equal(new DateOnly(2026, 9, 10), exemption.PublishedOn);
        Assert.Equal("Isenção - Locação de imóveis", exemption.Subject);
        Assert.Equal(
            "https://info.portaldasfinancas.gov.pt/pt/informacao_fiscal/informacoes_vinculativas/despesa/civa/Documents/PIV_30858.pdf",
            exemption.SourceUrl);

        var undated = result.Entries.Single(e => e.ProcessNumber == "29001");
        Assert.Null(undated.PublishedOn);
        Assert.Equal("Verba 1.12", undated.Article);
        Assert.Equal("Lista I", undated.Diploma);

        // "Anterior Artigo" is empty here, so the "Artigo" column is used.
        var freeForm = result.Entries.Single(e => e.FileName.StartsWith("Proc", StringComparison.Ordinal));
        Assert.Equal("civa-proc-2008-1234-transmissoes-intracomunitarias", freeForm.Id);
        Assert.Equal("14", freeForm.Article);
        Assert.Equal("Transmissões intracomunitárias & prova do transporte", freeForm.Subject);

        // Same file name as a CIRS ruling: a distinct id, because ids carry the library prefix.
        Assert.Contains(result.Entries, e => e.Id == "civa-piv_31329");
    }

    [Fact]
    public void RejectsPlainHttpOtherHostsNonPdfsAndRowsOfAnotherShapeWithTheReason()
    {
        var result = ListingParser.Parse(TestListing.ReadFixture("listing-civa.json"), Civa, Policy);

        Assert.Equal(4, result.Rejected.Count);
        Assert.Contains(result.Rejected, r => r.StartsWith("row 5: PDF link uses http, not https", StringComparison.Ordinal));
        Assert.Contains(result.Rejected, r => r.StartsWith("row 6: PDF link leaves the allowlisted host", StringComparison.Ordinal)
            && r.Contains("evil.example", StringComparison.Ordinal));
        Assert.Contains(result.Rejected, r => r.StartsWith("row 7: link is not a PDF", StringComparison.Ordinal));
        Assert.Contains(result.Rejected, r => r == "row 8: unexpected shape");
        Assert.All(result.Entries, e => Assert.StartsWith("https://info.portaldasfinancas.gov.pt/", e.SourceUrl));
    }

    public static TheoryData<string> Codes() => [.. TaxSource.All.Select(s => s.Code)];

    [Theory]
    [MemberData(nameof(Codes))]
    public void ParsesARowOfEveryLibrary(string code)
    {
        var source = TaxSource.Find(code)!;
        var json = TestListing.Json(
            TestListing.RowFor(source, TestListing.DocumentsPathFor(source) + "PIV_7.pdf", "7", "2025-01-02", "012-A", "Assunto"));

        var entry = Assert.Single(ListingParser.Parse(json, source, Policy).Entries);

        Assert.Equal(source.IdPrefix + "piv_7", entry.Id);
        Assert.Equal(code, entry.Tax);
        Assert.Equal(code, entry.Diploma);
        Assert.Equal("12-A", entry.Article);
        Assert.Equal("7", entry.ProcessNumber);
        Assert.Equal(new DateOnly(2025, 1, 2), entry.PublishedOn);
        Assert.Equal("Assunto", entry.Subject);
        Assert.Equal(TestListing.PdfUrlFor(source, "PIV_7.pdf"), entry.SourceUrl);
    }

    [Fact]
    public void EveryLibraryOfSourcesMdIsDefinedInOrderWithItsEndpoint()
    {
        Assert.Equal(
            ["CIRS", "CIRC", "DSRI", "EBF", "CIMI", "CIMT", "CIUC", "SELO", "CIVA", "RITI", "LGT", "CESE", "CSB"],
            TaxSource.All.Select(s => s.Code));
        var endpoints = TaxSource.All.ToDictionary(s => s.Code, s => (s.WebPath[RulingsRoot.Length..], s.PageItemId));
        Assert.Equal(("/rendimento/cirs", 42), endpoints["CIRS"]);
        Assert.Equal(("/rendimento/circ", 43), endpoints["CIRC"]);
        Assert.Equal(("/rendimento/DSRI", 27), endpoints["DSRI"]);
        Assert.Equal(("/beneficios_fiscais", 35), endpoints["EBF"]);
        Assert.Equal(("/patrimonio/cimi", 42), endpoints["CIMI"]);
        Assert.Equal(("/patrimonio/cimt", 40), endpoints["CIMT"]);
        Assert.Equal(("/patrimonio/ciuc", 30), endpoints["CIUC"]);
        Assert.Equal(("/patrimonio/selo", 37), endpoints["SELO"]);
        Assert.Equal(("/despesa/civa", 51), endpoints["CIVA"]);
        Assert.Equal(("/despesa/riti", 32), endpoints["RITI"]);
        Assert.Equal(("/Justica_Tributaria/LGT", 1), endpoints["LGT"]);
        Assert.Equal(("/Contribuicoes_extraordinarias/CESE", 1), endpoints["CESE"]);
        Assert.Equal(("/Contribuicoes_extraordinarias/CSB", 1), endpoints["CSB"]);

        Assert.All(TaxSource.All, source =>
        {
            Assert.True(Policy.IsAllowed(source.ListingUri));
            Assert.Contains(UnfilteredFilter, source.ListingUri.AbsoluteUri, StringComparison.Ordinal);
            Assert.Equal(source.Code.ToLowerInvariant(), source.DirectoryName);
            Assert.Equal(source == TaxSource.Cirs ? "" : source.DirectoryName + "-", source.IdPrefix);
            Assert.Equal(TaxSource.DocIconField, source.Fields[0]);
            Assert.All(source.Columns.All(), field => Assert.Contains(field, source.Fields));
        });
        Assert.Equal(TaxSource.All.Count, TaxSource.All.Select(s => s.DirectoryName).Distinct().Count());
    }

    [Fact]
    public void PerLibraryFieldDifferencesAreMappedExplicitly()
    {
        var columns = TaxSource.All.ToDictionary(s => s.Code, s => s.Columns);

        Assert.Equal(["Artigo0"], columns["EBF"].Article);
        Assert.Equal("N_x00ba__x002f_Al_x00ed_nea", columns["EBF"].Paragraph);
        Assert.Equal("N_x00ba__x002f_Al_x00ed_nea", columns["CIMI"].Paragraph);
        Assert.Equal("Vinc_x002e__x0020_n_x002e__x00ba_", columns["CIVA"].ProcessNumber);
        Assert.Equal("Data", columns["CIVA"].PublishedOn);
        Assert.Equal(["Anterior_x0020_Artigo", "Artigo"], columns["CIVA"].Article);
        Assert.Equal("Vinc_x002e__x0020_n_x002e__x00ba_", columns["RITI"].ProcessNumber);
        Assert.Null(columns["RITI"].Diploma);
        Assert.Null(columns["CESE"].Diploma);
        Assert.Null(columns["CESE"].Paragraph);
        Assert.Null(columns["CSB"].Diploma);
        Assert.Equal(["Artigo"], columns["CIRC"].Article);
        Assert.Equal(columns["CIRS"], columns["LGT"]);
    }

    [Fact]
    public void CivaListingUriUsesTheSupersetFieldsOfSourcesMd()
    {
        Assert.Equal(
            "https://info.portaldasfinancas.gov.pt/pt/informacao_fiscal/informacoes_vinculativas/despesa/civa/_vti_bin/portalat/docs.svc/listdocs"
            + "?fields=DocIcon,Vinc_x002e__x0020_n_x002e__x00ba_,Data,Diploma,Anterior_x0020_Artigo,Assunto,N_x002e__x00ba__x002f_Al_x00ed_nea,Assunto_Resumo,Artigo"
            + "&sort=Vinc_x002e__x0020_n_x002e__x00ba_:DESC,Data:DESC&filter=" + UnfilteredFilter + "&id=51",
            Civa.ListingUri.AbsoluteUri);
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
