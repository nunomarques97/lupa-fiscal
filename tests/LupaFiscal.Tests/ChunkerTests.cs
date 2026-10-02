using LupaFiscal.Core.Indexing;
using LupaFiscal.Tests.Support;

namespace LupaFiscal.Tests;

public sealed class ChunkerTests
{
    private readonly FakeEmbedder _counter = new();

    private Chunker NewChunker(int maxTokens = 512, int overlap = 64) =>
        new(_counter, new ChunkingOptions { MaxTokens = maxTokens, OverlapTokens = overlap });

    private static string Body(TestCorpus.Ruling ruling) =>
        RulingSections.NormalizeBody(ruling.Text.Replace("\r\n", "\n", StringComparison.Ordinal));

    [Fact]
    public void NormalisingRemovesPageFurnitureAndBlankLines()
    {
        var body = Body(TestCorpus.Education);

        Assert.DoesNotContain("INFORMAÇÃO VINCULATIVA", body, StringComparison.Ordinal);
        Assert.DoesNotContain("FICHA DOUTRINÁRIA", body, StringComparison.Ordinal);
        Assert.DoesNotContain("1Processo: 90001", body, StringComparison.Ordinal);
        Assert.DoesNotContain("\n\n", body, StringComparison.Ordinal);
        // The metadata line with the decision is kept.
        Assert.Contains("Processo: 90001, com despacho de 2024-03-01", body, StringComparison.Ordinal);
        Assert.DoesNotContain("90003/2010  1", Body(TestCorpus.Rental), StringComparison.Ordinal);
    }

    [Fact]
    public void SplitsAtTheSectionHeadingsFoundInTheCorpus()
    {
        var body = Body(TestCorpus.Education);

        var sections = RulingSections.Split(body);

        Assert.Equal(["header", "request", "facts", "legal-framework", "conclusion"], sections.Select(s => s.Name));
        Assert.StartsWith("Diploma:", body[sections[0].Start..sections[0].End], StringComparison.Ordinal);
        Assert.EndsWith("Processo: 90001, com despacho de 2024-03-01, da Diretora de Serviços", body[sections[0].Start..sections[0].End], StringComparison.Ordinal);
        Assert.StartsWith("O requerente pretende saber", body[sections[1].Start..sections[1].End], StringComparison.Ordinal);
        Assert.StartsWith("FACTOS\n", body[sections[2].Start..sections[2].End], StringComparison.Ordinal);
        // The facts section continues across the page break; the next page's banner is gone.
        Assert.EndsWith("com o NIF dos dependentes.", body[sections[2].Start..sections[2].End], StringComparison.Ordinal);
        Assert.StartsWith("INFORMAÇÃO\n1. Nos termos", body[sections[3].Start..sections[3].End], StringComparison.Ordinal);
        Assert.StartsWith("CONCLUSÃO\n", body[sections[4].Start..sections[4].End], StringComparison.Ordinal);
        for (var i = 1; i < sections.Count; i++) Assert.True(sections[i].Start >= sections[i - 1].End);
    }

    [Fact]
    public void RecognisesNumberedHeadingsAndJoinsAnEmptyHeadingToTheNextSection()
    {
        var sections = RulingSections.Split(Body(TestCorpus.CapitalGains));
        Assert.Equal(["header", "request", "facts", "legal-framework"], sections.Select(s => s.Name));

        var body = "Conteúdo: PEDIDO:\nFACTOS\nO requerente vendeu um imóvel.\nCONCLUSÕES\nNão há tributação.";
        var joined = RulingSections.Split(body);
        Assert.Equal(["facts", "conclusion"], joined.Select(s => s.Name));
        Assert.StartsWith("PEDIDO:\nFACTOS", body[joined[0].Start..joined[0].End], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("INFORMAÇÃO", "legal-framework")]
    [InlineData("INFORMAÇÂO:", "legal-framework")]
    [InlineData("II - INFORMAÇÃO", "legal-framework")]
    [InlineData("ENQUADRAMENTO JURÍDICO-TRIBUTÁRIO:", "legal-framework")]
    [InlineData("Enquadramento:", "legal-framework")]
    [InlineData("PARECER", "legal-framework")]
    [InlineData("I - DESCRIÇÃO DOS FACTOS", "facts")]
    [InlineData("DOS FACTOS", "facts")]
    [InlineData("Factos relevantes:", "facts")]
    [InlineData("O PEDIDO:", "request")]
    [InlineData("III.CONCLUSÃO", "conclusion")]
    [InlineData("Conclusões", "conclusion")]
    [InlineData("informação:", null)]
    [InlineData("Informa-se:", null)]
    [InlineData("pedido);", null)]
    [InlineData("O requerente apresentou o pedido de informação.", null)]
    public void ClassifiesHeadingLines(string line, string? expected) =>
        Assert.Equal(expected, RulingSections.HeadingName(line));

    [Fact]
    public void ARulingWithoutRecognisableSectionsIsOneContentSection()
    {
        var body = Body(TestCorpus.Pension);

        var sections = RulingSections.Split(body);
        var chunks = NewChunker().Split(body);

        var section = Assert.Single(sections);
        Assert.Equal("content", section.Name);
        Assert.Equal((0, body.Length), (section.Start, section.End));
        var chunk = Assert.Single(chunks);
        Assert.Equal("content", chunk.Section);
        Assert.Equal(body, chunk.Text);
    }

    [Fact]
    public void ChunksNeverCrossSectionsAndKeepTheirOffsets()
    {
        var body = Body(TestCorpus.Education);
        var sections = RulingSections.Split(body);

        var chunks = NewChunker().Split(body);

        Assert.Equal(sections.Select(s => s.Name), chunks.Select(c => c.Section));
        for (var i = 0; i < chunks.Count; i++)
        {
            var chunk = chunks[i];
            Assert.Equal(i, chunk.Ordinal);
            Assert.Equal(body[chunk.Start..chunk.End], chunk.Text);
            var section = sections[i];
            Assert.True(chunk.Start >= section.Start && chunk.End <= section.End);
            Assert.Equal(_counter.CountPassageTokens(chunk.Text), chunk.TokenCount);
        }
    }

    [Fact]
    public void ALongSectionIsSplitWithOverlapWithinTheTokenBudget()
    {
        var sentences = Enumerable.Range(1, 120).Select(i => $"A frase número {i} descreve a regra aplicável ao rendimento {i}.");
        var body = "Conteúdo: FACTOS\n" + string.Join(" ", sentences) + "\nCONCLUSÃO\nA regra aplica-se.";
        const int Max = 128;
        const int Overlap = 24;

        var chunks = NewChunker(Max, Overlap).Split(body);

        var facts = chunks.Where(c => c.Section == "facts").ToList();
        Assert.True(facts.Count >= 5, $"expected several chunks, got {facts.Count}");
        Assert.All(chunks, c => Assert.True(c.TokenCount <= Max, $"chunk of {c.TokenCount} tokens"));
        Assert.Equal("conclusion", chunks[^1].Section);
        Assert.True(chunks[^1].Start > facts[^1].End, "the overlap never crosses a section boundary");
        for (var i = 1; i < facts.Count; i++)
        {
            // Each chunk starts inside the previous one (overlap) and still moves forward.
            Assert.True(facts[i].Start < facts[i - 1].End, $"chunk {i} does not overlap the previous one");
            Assert.True(facts[i].Start > facts[i - 1].Start);
            var overlap = body[facts[i].Start..facts[i - 1].End];
            Assert.True(_counter.CountTokens(overlap) <= Overlap, $"overlap of {_counter.CountTokens(overlap)} tokens");
            Assert.StartsWith("A frase número", facts[i].Text, StringComparison.Ordinal); // whole sentences
        }
        // Together the chunks cover the whole section.
        var section = RulingSections.Split(body).First(s => s.Name == "facts");
        Assert.Equal(section.Start, facts[0].Start);
        Assert.Equal(section.End, facts[^1].End);
    }

    [Fact]
    public void AVeryLongSentenceWithoutPunctuationIsCutAtWordBoundaries()
    {
        var words = string.Join(" ", Enumerable.Range(1, 3000).Select(i => $"palavra{i}"));
        var body = "Conteúdo: " + words;

        var chunks = NewChunker().Split(body);

        Assert.True(chunks.Count >= 6);
        Assert.All(chunks, c =>
        {
            Assert.True(c.TokenCount <= 512);
            Assert.Equal(body[c.Start..c.End], c.Text);
            Assert.Matches(@"^palavra\d+$", c.Text.Split(' ')[0]);
            Assert.Matches(@"^palavra\d+$", c.Text.Split(' ')[^1]);
        });
        Assert.Equal("palavra3000", chunks[^1].Text.Split(' ')[^1]);
        for (var i = 1; i < chunks.Count; i++) Assert.True(chunks[i].Start < chunks[i - 1].End);
    }

    [Fact]
    public void AWordLongerThanTheWindowIsStillCutToFit()
    {
        var body = "Conteúdo: " + string.Concat(Enumerable.Repeat("ab-", 400));

        var chunks = NewChunker(maxTokens: 100, overlap: 10).Split(body);

        Assert.True(chunks.Count > 1);
        Assert.All(chunks, c => Assert.True(c.TokenCount <= 100, $"chunk of {c.TokenCount} tokens"));
        Assert.Equal(body.Length, chunks[^1].End);
    }

    [Fact]
    public void AnEmptyBodyHasNoChunks() => Assert.Empty(NewChunker().Split(""));

    [Fact]
    public void RejectsAWindowLargerThanTheModel() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => NewChunker(maxTokens: 1024));
}
