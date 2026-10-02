using LupaFiscal.Core.Extraction;
using LupaFiscal.Tests.Support;

namespace LupaFiscal.Tests;

public class PdfTextExtractorTests
{
    private readonly PdfTextExtractor _extractor = new();

    [Fact]
    public void ExtractsTheTextLayerWithPortugueseCharacters()
    {
        var result = _extractor.Extract(TestPdfs.Ruling());

        Assert.True(result.HasTextLayer);
        Assert.Equal(1, result.PageCount);
        Assert.Contains(TestPdfs.Expect("INFORMAÇÃO VINCULATIVA"), result.Text, StringComparison.Ordinal);
        Assert.Contains(TestPdfs.Expect("habitação própria"), result.Text, StringComparison.Ordinal);
        Assert.Contains("II. FACTOS DESCRITOS NO PEDIDO", result.Text, StringComparison.Ordinal);
        Assert.True(result.TextChars >= PdfTextExtractor.MinTextChars);
    }

    [Fact]
    public void FindsTheProcessNumberAndDecisionDate()
    {
        var result = _extractor.Extract(TestPdfs.Ruling());

        Assert.Equal("31329", result.ProcessNumber);
        Assert.Equal(new DateOnly(2026, 9, 22), result.DecisionDate);
    }

    [Fact]
    public void EmptyTextPdfHasNoTextLayer()
    {
        var result = _extractor.Extract(TestPdfs.EmptyPages());

        Assert.False(result.HasTextLayer);
        Assert.Equal(2, result.PageCount);
        Assert.Equal(0, result.TextChars);
    }

    [Fact]
    public void ImageOnlyPdfHasNoTextLayer()
    {
        var result = _extractor.Extract(TestPdfs.ImageOnly());

        Assert.False(result.HasTextLayer);
        Assert.Equal(0, result.TextChars);
    }

    [Fact]
    public void TextBelowTheThresholdCountsAsScanned()
    {
        var result = _extractor.Extract(TestPdfs.WithText("Página 1", "Digitalizado"));

        Assert.False(result.HasTextLayer);
        Assert.InRange(result.TextChars, 1, PdfTextExtractor.MinTextChars - 1);
    }

    [Fact]
    public void MalformedPdfThrows()
    {
        Assert.ThrowsAny<Exception>(() => _extractor.Extract("%PDF-1.5 not really a pdf"u8.ToArray()));
    }

    [Theory]
    [InlineData("Processo: 31329, com despacho de 2026-09-22, da Diretora", "31329")]
    [InlineData("Processo n.º 2006 000123, sancionado por despacho", "2006 000123")]
    [InlineData("Processo: 4567 com despacho de 2020-01-02", "4567")]
    [InlineData("Processo: 2184/03  1\nAssunto: x\nProcesso: 2184/03, com despacho concordante do Sr. Subdirector-Geral", "2184/03")]
    [InlineData("Processo: 2210/2010  1\nConteúdo", "2210/2010")]
    [InlineData("Processo:  1\nProcesso: 2998/2008, com despacho concordante", "2998/2008")]
    [InlineData("Sem número de processo", null)]
    public void ParsesProcessNumberVariants(string text, string? expected)
    {
        Assert.Equal(expected, PdfTextExtractor.FindProcessNumber(text));
    }

    [Fact]
    public void CleaningTurnsNoBreakSpacesAndSoftHyphensIntoPlainCharacters()
    {
        var cleaned = PdfTextExtractor.Clean("Processo: 5957/2010, com despacho da Subdirectora­Geral\t \r\nfim\0");

        Assert.Equal("Processo: 5957/2010, com despacho da Subdirectora-Geral\nfim", cleaned);
        Assert.Equal("5957/2010", PdfTextExtractor.FindProcessNumber(cleaned));
    }

    [Theory]
    [InlineData("com despacho de 2026-09-22, da", 2026, 9, 22)]
    [InlineData("por despacho de 14-05-2019 do", 2019, 5, 14)]
    [InlineData("com despacho concordante da Subdirectora-Geral de 2010-05-05", 2010, 5, 5)]
    [InlineData("com despacho concordante do Sr. Subdirector-Geral, de\n2003-05-05, nos termos", 2003, 5, 5)]
    public void ParsesDecisionDates(string text, int year, int month, int day)
    {
        Assert.Equal(new DateOnly(year, month, day), PdfTextExtractor.FindDecisionDate(text));
    }
}
