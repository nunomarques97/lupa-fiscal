using LupaFiscal.Core.Corpus;
using LupaFiscal.Core.Crawling;
using LupaFiscal.Tests.Support;

namespace LupaFiscal.Tests;

public class RulingIdAndStoreTests
{
    [Theory]
    [InlineData("PIV_31329.pdf", "piv_31329")]
    [InlineData("PIV_12875_Premios_competicoes_desportivas_columbofilas.pdf", "piv_12875_premios_competicoes_desportivas_columbofilas")]
    [InlineData("Ficha Doutrinária - Proc nº 2006 000123.pdf", "ficha-doutrinaria-proc-no-2006-000123")]
    [InlineData("../../../Windows/System32/evil.pdf", "windows-system32-evil")]
    [InlineData("..\\..\\evil.pdf", "evil")]
    [InlineData("CON.pdf", "con_")]
    [InlineData("....pdf", "ruling-")]
    public void SanitisesFileNames(string fileName, string expectedPrefix)
    {
        var id = RulingId.FromFileName(fileName);

        Assert.StartsWith(expectedPrefix, id);
        Assert.True(RulingId.IsValid(id), id);
    }

    [Fact]
    public void LongNamesAreTruncatedWithAHash()
    {
        var id = RulingId.FromFileName(new string('a', 300) + ".pdf");

        Assert.True(id.Length <= RulingId.MaxLength + 9);
        Assert.True(RulingId.IsValid(id));
        Assert.NotEqual(id, RulingId.FromFileName(new string('a', 301) + ".pdf"));
    }

    [Theory]
    [InlineData("../evil")]
    [InlineData("..")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("C:evil")]
    [InlineData("nul")]
    [InlineData("")]
    [InlineData("UPPER")]
    public void StoreRejectsIdsThatAreNotSanitised(string id)
    {
        using var temp = new TempDirectory();
        var store = new CorpusStore(temp.Path, TaxSource.Cirs);

        Assert.Throws<ArgumentException>(() => store.PdfPath(id));
        Assert.Throws<ArgumentException>(() => store.WritePdf(id, [1]));
        Assert.Empty(Directory.EnumerateFiles(temp.Path, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public void CachePathsStayInsideTheTaxDirectory()
    {
        using var temp = new TempDirectory();
        var store = new CorpusStore(temp.Path, TaxSource.Cirs);
        var id = RulingId.FromFileName("../../../../evil.pdf");

        store.WritePdf(id, [1, 2, 3]);

        var file = Assert.Single(Directory.EnumerateFiles(temp.Path, "*", SearchOption.AllDirectories));
        Assert.StartsWith(Path.Combine(temp.Path, "cirs", "pdf") + Path.DirectorySeparatorChar, file);
        Assert.Equal("evil.pdf", Path.GetFileName(file));
    }

    [Fact]
    public void ManifestWithAnInvalidIdIsRejected()
    {
        using var temp = new TempDirectory();
        var store = new CorpusStore(temp.Path, TaxSource.Cirs);
        Directory.CreateDirectory(store.TaxDirectory);
        File.WriteAllText(store.ManifestPath,
            """{"version":1,"tax":"CIRS","rulings":[{"id":"../x","fileName":"x.pdf","sourceUrl":"https://info.portaldasfinancas.gov.pt/x.pdf","tax":"CIRS"}]}""");

        Assert.Throws<InvalidDataException>(() => store.LoadManifest());
    }

    [Fact]
    public void ManifestRoundTripsStateAndMetadata()
    {
        using var temp = new TempDirectory();
        var store = new CorpusStore(temp.Path, TaxSource.Cirs);
        var manifest = new CorpusManifest { Tax = "CIRS" };
        manifest.Rulings.Add(new RulingRecord
        {
            Id = "piv_1",
            FileName = "PIV_1.pdf",
            SourceUrl = TestListing.PdfUrl("PIV_1.pdf"),
            Tax = "CIRS",
            Article = "12-B",
            PublishedOn = new DateOnly(2025, 1, 2),
            State = RulingState.ScannedSkipped,
            Reason = "no text",
        });

        store.SaveManifest(manifest);
        var loaded = store.LoadManifest()!;

        var ruling = Assert.Single(loaded.Rulings);
        Assert.Equal(RulingState.ScannedSkipped, ruling.State);
        Assert.Equal("12-B", ruling.Article);
        Assert.Equal(new DateOnly(2025, 1, 2), ruling.PublishedOn);
        Assert.Contains("\"scannedSkipped\"", File.ReadAllText(store.ManifestPath), StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(store.ManifestPath + ".partial"));
    }
}
