using LupaFiscal.Core.Answering;

namespace LupaFiscal.Tests;

public sealed class CitationEnforcerTests
{
    private static readonly IReadOnlySet<int> Five = new HashSet<int> { 1, 2, 3, 4, 5 };

    private static EnforcedAnswer Enforce(string? output, bool complete = true) => CitationEnforcer.Enforce(output, Five, complete);

    private static List<string> Texts(EnforcedAnswer answer) => answer.Sentences.Select(s => s.Text).ToList();

    [Fact]
    public void AFullyValidAnswerKeepsEverySentenceWithItsIds()
    {
        var answer = Enforce("As despesas de educação são dedutíveis à coleta [1]. O limite é de 800 euros por agregado [2][4].\n" +
            "As propinas contam como despesas de educação [1, 3].");

        Assert.False(answer.Refused);
        Assert.Equal(["As despesas de educação são dedutíveis à coleta.", "O limite é de 800 euros por agregado.",
            "As propinas contam como despesas de educação."], Texts(answer));
        Assert.Equal([[1], [2, 4], [1, 3]], answer.Sentences.Select(s => s.Citations.ToArray()));
        Assert.Equal((3, 3), (answer.RawSentences, answer.ValidRawSentences));
        Assert.Equal("As despesas de educação são dedutíveis à coleta. [1] O limite é de 800 euros por agregado. [2][4] " +
            "As propinas contam como despesas de educação. [1][3]", answer.Text);
    }

    [Fact]
    public void ASentenceWithoutACitationIsRemoved()
    {
        var answer = Enforce("Sim, pode deduzir. As propinas são dedutíveis à coleta [1]. Consulte sempre um contabilista.");

        Assert.Equal(["As propinas são dedutíveis à coleta."], Texts(answer));
        Assert.Equal((3, 1), (answer.RawSentences, answer.ValidRawSentences));
    }

    [Theory]
    [InlineData("As propinas são dedutíveis à coleta [6].")]
    [InlineData("As propinas são dedutíveis à coleta [0].")]
    [InlineData("As propinas são dedutíveis à coleta [12].")]
    [InlineData("As propinas são dedutíveis à coleta [1][9].")]
    [InlineData("As propinas são dedutíveis à coleta [2, 7].")]
    [InlineData("As propinas são dedutíveis à coleta [99999].")]
    public void ASentenceCitingAnIdThatWasNotProvidedIsRemovedEvenWithAValidOne(string output)
    {
        var answer = Enforce(output);

        Assert.True(answer.Refused);
        Assert.Empty(answer.Sentences);
        Assert.Equal((1, 0), (answer.RawSentences, answer.ValidRawSentences));
        Assert.Equal(CitationEnforcer.RefusalText, answer.Text);
    }

    [Theory]
    [InlineData("As propinas são dedutíveis à coleta [1.")]
    [InlineData("As propinas são dedutíveis à coleta 1].")]
    [InlineData("As propinas são dedutíveis à coleta [1-3].")]
    [InlineData("As propinas são dedutíveis à coleta [fonte 1].")]
    [InlineData("As propinas são dedutíveis à coleta [a].")]
    [InlineData("As propinas são dedutíveis à coleta [].")]
    [InlineData("As propinas são dedutíveis [1] à coleta [[2]].")]
    [InlineData("As propinas são dedutíveis à coleta (1).")]
    [InlineData("As propinas são dedutíveis à coleta (1). [1]")]
    [InlineData("As propinas são dedutíveis à coleta (1, 2) [1].")]
    [InlineData("As propinas são dedutíveis à coleta (2 e 3) [1].")]
    [InlineData("As propinas são dedutíveis à coleta (Fonte) [1].")]
    [InlineData("As propinas são dedutíveis à coleta (fonte 1) [1].")]
    [InlineData("As propinas são dedutíveis à coleta (Fontes 1 e 4) [1].")]
    [InlineData("As propinas são dedutíveis à coleta ¹.")]
    public void MalformedOrNonBracketCitationsDoNotCount(string output)
    {
        var answer = Enforce(output + " O limite legal aplica-se ao agregado [2].");

        Assert.Equal(["O limite legal aplica-se ao agregado."], Texts(answer));
        Assert.Equal((2, 1), (answer.RawSentences, answer.ValidRawSentences));
    }

    [Fact]
    public void ParenthesesThatAreNotCitationsAreKept()
    {
        var answer = Enforce("A alínea a) do n.º 1 do artigo 10.º (mais-valias) aplica-se aos imóveis (incluindo frações) [1].");

        Assert.Equal(["A alínea a) do n.º 1 do artigo 10.º (mais-valias) aplica-se aos imóveis (incluindo frações)."], Texts(answer));
    }

    [Fact]
    public void PortugueseAbbreviationsDoNotEndASentence()
    {
        var answer = Enforce("Nos termos do art. 78.º-D, n.º 1, al. a), do CIRS, as despesas são dedutíveis [1]. " +
            "O disposto nos arts. 13.º e 22.º, cfr. o n.º 2, aplica-se ao agregado [2]. A sociedade X, S.A. não é residente [3].");

        Assert.Equal(["Nos termos do art. 78.º-D, n.º 1, al. a), do CIRS, as despesas são dedutíveis.",
            "O disposto nos arts. 13.º e 22.º, cfr. o n.º 2, aplica-se ao agregado.",
            "A sociedade X, S.A. não é residente."], Texts(answer));
    }

    [Fact]
    public void ASentenceEndingInAnArticleSuffixStillEnds()
    {
        var answer = Enforce("A dedução está prevista no artigo 78.º-D. Aplica-se às propinas pagas [1].");

        Assert.Equal(["Aplica-se às propinas pagas."], Texts(answer));
        Assert.Equal((2, 1), (answer.RawSentences, answer.ValidRawSentences));
    }

    [Fact]
    public void ACitationWrittenAfterTheFullStopBelongsToThatSentence()
    {
        var answer = Enforce("As propinas são dedutíveis. [1] O limite é de 800 euros por agregado.[2][3]");

        Assert.Equal(["As propinas são dedutíveis.", "O limite é de 800 euros por agregado."], Texts(answer));
        Assert.Equal([[1], [2, 3]], answer.Sentences.Select(s => s.Citations.ToArray()));
    }

    [Fact]
    public void MarkupAndHtmlAreStrippedToPlainText()
    {
        var answer = Enforce("""
            ### Resposta
            **As propinas** são <b>dedutíveis</b> à coleta [1].
            - <script>alert('x')</script>O limite &amp; as condições estão no `artigo 78.º-D` [2].
            1. Ver [a ficha](https://example.com/x) para os detalhes completos [3].
            <img src=x onerror=alert(1)>Texto com &lt;i&gt;entidades&lt;/i&gt; codificadas [4].
            """);

        Assert.Equal(["As propinas são dedutíveis à coleta.", "alert('x') O limite & as condições estão no artigo 78.º-D.",
            "Ver a ficha para os detalhes completos.", "Texto com entidades codificadas."], Texts(answer));
        Assert.All(answer.Sentences, s => Assert.DoesNotMatch("[<>*#`]", s.Text));
        Assert.Equal(4, answer.ValidRawSentences);
    }

    [Fact]
    public void AReasoningBlockIsNeverShown()
    {
        var answer = Enforce("<think>A fonte [1] diz que sim.</think>As propinas são dedutíveis à coleta [1].");

        Assert.Equal(["As propinas são dedutíveis à coleta."], Texts(answer));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \n\n  ")]
    [InlineData("[1][2].")]
    public void EmptyOutputGivesTheRefusal(string? output)
    {
        var answer = Enforce(output);

        Assert.True(answer.Refused);
        Assert.Empty(answer.Sentences);
        Assert.Equal(0, answer.RawSentences);
        Assert.Equal(CitationEnforcer.RefusalText, answer.Text);
    }

    [Fact]
    public void AllInvalidOutputGivesTheRefusal()
    {
        var answer = Enforce("Não encontrei a resposta nas fontes. Segundo a fonte [7], sim. Fontes: [1], [2].");

        Assert.True(answer.Refused);
        Assert.Equal((3, 0), (answer.RawSentences, answer.ValidRawSentences));
        Assert.StartsWith("Não ", CitationEnforcer.RefusalText, StringComparison.Ordinal);
    }

    [Fact]
    public void ASentenceContainingARejectedOneIsDroppedWhateverItsCaseAndPunctuation()
    {
        var answer = CitationEnforcer.Enforce("PRIMEIRA afirmação, tirada da fonte dois [2]. A primeira afirmação tirada da fonte dois vale [2]. " +
            "Uma primeira afirmação tirada da fonte doisx conta [2].", Five, rejected: ["Primeira afirmação tirada da fonte dois"]);

        Assert.Equal(["Uma primeira afirmação tirada da fonte doisx conta."], Texts(answer));
        Assert.Equal((3, 1), (answer.RawSentences, answer.ValidRawSentences));
    }

    [Fact]
    public void ARepeatedSentenceIsShownOnceAndStillCountsAsValid()
    {
        var answer = Enforce("O valor não é dedutível [1].\n\nO valor não é dedutível [1].\nO valor, não é dedutível [1]. Outra regra aplica-se [2].");

        Assert.Equal(["O valor não é dedutível.", "Outra regra aplica-se."], Texts(answer));
        Assert.Equal((4, 4), (answer.RawSentences, answer.ValidRawSentences));
    }

    [Fact]
    public void ALabelOrAFragmentIsNotASentence()
    {
        var answer = Enforce("Fontes: [1] [2]\nSim [1].\nAs propinas são dedutíveis [1].");

        Assert.Equal(["As propinas são dedutíveis."], Texts(answer));
        Assert.Equal(3, answer.RawSentences);
    }

    [Fact]
    public void AnUnfinishedLastSentenceIsDroppedWhenGenerationHitALimit()
    {
        const string Output = "As propinas são dedutíveis à coleta [1]. O limite aplica-se [2] por cada dependente que";

        Assert.Equal(["As propinas são dedutíveis à coleta."], Texts(Enforce(Output, complete: false)));
        Assert.Equal(["As propinas são dedutíveis à coleta.", "O limite aplica-se por cada dependente que."], Texts(Enforce(Output)));
    }

    [Fact]
    public void RepeatedIdsInOneSentenceAreListedOnce()
    {
        var answer = Enforce("As propinas são dedutíveis [2] e o material escolar também [2][1].");

        Assert.Equal([2, 1], answer.Sentences.Single().Citations);
    }

    [Fact]
    public void EveryShownIdWasProvided()
    {
        var provided = new HashSet<int> { 1, 2 };
        var outputs = new[]
        {
            "Uma frase válida com fonte [1]. Outra frase inválida [3]. Mais uma válida aqui [2][1].",
            "Frase [1] com duas citações [4] no meio. E outra frase sem nada.",
            "[1] Frase que começa pela citação e acaba sem ela.",
        };

        foreach (var output in outputs)
        {
            var answer = CitationEnforcer.Enforce(output, provided);
            Assert.All(answer.Sentences, s => Assert.All(s.Citations, id => Assert.Contains(id, provided)));
            Assert.All(answer.Sentences, s => Assert.NotEmpty(s.Citations));
        }
    }
}
