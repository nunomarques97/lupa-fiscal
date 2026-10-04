using LupaFiscal.Core.Answering;
using LupaFiscal.Core.Indexing;
using LupaFiscal.Core.Search;
using LupaFiscal.Tests.Support;

namespace LupaFiscal.Tests;

public sealed class AnswererTests : IDisposable
{
    private const string Question = "Posso deduzir as despesas de educação dos meus filhos?";

    private readonly TempDirectory _temp = new();
    private readonly FakeEmbedder _embedder = new();

    public AnswererTests()
    {
        var store = TestCorpus.Write(_temp.Path, TestCorpus.All);
        using var database = IndexDatabase.OpenForWrite(IndexPath);
        new IndexBuilder(database, _embedder, new ChunkingOptions(), TextWriter.Null).Build(store, CancellationToken.None);
    }

    public void Dispose() => _temp.Dispose();

    private string IndexPath => Path.Combine(_temp.Path, "lupa-fiscal.db");

    private Answerer Answerer(FakeAnswerGenerator generator) => new(HybridSearcher.Open(IndexPath, _embedder), generator);

    [Fact]
    public void ThePromptHoldsTheSameTopPassagesAsSearchOnePerRuling()
    {
        var generator = new FakeAnswerGenerator();
        var answerer = Answerer(generator);

        var answer = answerer.Answer(Question, new SearchFilters(), CancellationToken.None);

        var search = HybridSearcher.Open(IndexPath, _embedder).Search(Question, new SearchFilters(),
            new SearchOptions { Limit = answerer.Options.Prompt.MaxPassages });
        Assert.Equal(search.Hits.Select(h => (h.ChunkId, h.RulingId)), answer.Provided.Select(c => (c.Hit.ChunkId, c.Hit.RulingId)));
        Assert.Equal(answer.Provided.Select(c => c.Hit.RulingId).Distinct().Count(), answer.Provided.Count);
        Assert.Equal(Enumerable.Range(1, answer.Provided.Count), answer.Provided.Select(c => c.Id));
        Assert.Single(generator.Requests);
        Assert.Equal(answerer.Options.Prompt.MaxNewTokens, generator.Requests[0].MaxNewTokens);
    }

    [Fact]
    public void AValidAnswerCitesOnlyProvidedPassagesAndListsEachCitedOne()
    {
        var answer = Answerer(new FakeAnswerGenerator()).Answer(Question, new SearchFilters(), CancellationToken.None);

        Assert.Equal(AnswerStatus.Answered, answer.Status);
        var sentence = Assert.Single(answer.Sentences);
        Assert.Equal([1], sentence.Citations);
        Assert.StartsWith("Segundo a fonte, ", sentence.Text, StringComparison.Ordinal);
        var citation = Assert.Single(answer.Citations);
        Assert.Same(answer.Provided[0], citation);
        Assert.Equal(FakeAnswerGenerator.FakeModelId, answer.ModelId);
        Assert.Equal((1, 1), (answer.RawSentences, answer.ValidRawSentences));
    }

    [Fact]
    public void UnknownIdsAndUncitedSentencesNeverReachTheAnswer()
    {
        var generator = new FakeAnswerGenerator(_ => "Pode deduzir sempre as despesas. Há uma regra na fonte [9]. " +
            "As despesas de educação são dedutíveis à coleta [1][2]. E ainda [1][7] outra coisa.");

        var answer = Answerer(generator).Answer(Question, new SearchFilters(), CancellationToken.None);

        Assert.Equal(["As despesas de educação são dedutíveis à coleta."], answer.Sentences.Select(s => s.Text));
        Assert.Equal([1, 2], answer.Citations.Select(c => c.Id));
        Assert.Equal((4, 1), (answer.RawSentences, answer.ValidRawSentences));
        Assert.All(answer.Sentences.SelectMany(s => s.Citations), id => Assert.Contains(answer.Citations, c => c.Id == id));
    }

    [Fact]
    public void ACopyOfThePromptFormatExampleIsNeverShownEvenWithAValidId()
    {
        var generator = new FakeAnswerGenerator(_ => "Primeira afirmação tirada da fonte dois [2]. " +
            "segunda afirmação, apoiada pelas fontes um e três [1][3]. As despesas de educação são dedutíveis à coleta [1].");

        var answer = Answerer(generator).Answer(Question, new SearchFilters(), CancellationToken.None);

        Assert.Equal(["As despesas de educação são dedutíveis à coleta."], answer.Sentences.Select(s => s.Text));
        Assert.Equal((2, 1), (answer.RawSentences, answer.ValidRawSentences));
    }

    [Fact]
    public void AnAllInvalidOutputIsTheFixedRefusal()
    {
        var answer = Answerer(new FakeAnswerGenerator(_ => "Não encontrei a resposta nas fontes."))
            .Answer(Question, new SearchFilters(), CancellationToken.None);

        Assert.Equal(AnswerStatus.Refused, answer.Status);
        Assert.Empty(answer.Sentences);
        Assert.Empty(answer.Citations);
        Assert.NotEmpty(answer.Provided);
        Assert.Equal(CitationEnforcer.RefusalText, answer.Text);
    }

    [Fact]
    public void WithoutPassagesTheModelIsNotCalledAndTheAnswerIsTheRefusal()
    {
        var generator = new FakeAnswerGenerator();

        var answer = Answerer(generator).Answer(Question, new SearchFilters(Tax: "CIVA"), CancellationToken.None);

        Assert.Equal(AnswerStatus.Refused, answer.Status);
        Assert.Empty(answer.Provided);
        Assert.Empty(generator.Requests);
        Assert.Null(answer.Stop);
    }

    [Fact]
    public void FiltersApplyToThePassagesAndAnArticleNeedsATax()
    {
        var generator = new FakeAnswerGenerator();
        var answerer = Answerer(generator);

        var answer = answerer.Answer(Question, new SearchFilters("CIRS", "10"), CancellationToken.None);

        Assert.All(answer.Provided, c => Assert.Equal("piv_90002", c.Hit.RulingId));
        Assert.Throws<ArgumentException>(() => answerer.Answer(Question, new SearchFilters(Article: "10"), CancellationToken.None));
    }

    [Fact]
    public void AnAnswerCutByALimitDropsItsUnfinishedLastSentence()
    {
        var generator = new FakeAnswerGenerator(_ => "As despesas de educação são dedutíveis [1]. O limite aplica-se [1] a cada");
        generator.Stop = GenerationStop.TokenLimit;

        var answer = Answerer(generator).Answer(Question, new SearchFilters(), CancellationToken.None);

        Assert.Equal(["As despesas de educação são dedutíveis."], answer.Sentences.Select(s => s.Text));
        Assert.Equal(GenerationStop.TokenLimit, answer.Stop);
    }

    [Fact]
    public void ACancelledRequestThrowsAndDoesNotGenerate()
    {
        var generator = new FakeAnswerGenerator();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() => Answerer(generator).Answer(Question, new SearchFilters(), cancelled.Token));
        Assert.Empty(generator.Requests);
    }
}
