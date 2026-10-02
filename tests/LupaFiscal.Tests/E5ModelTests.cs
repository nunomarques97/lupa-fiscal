using System.Numerics.Tensors;
using System.Text;
using System.Text.Json;
using LupaFiscal.Core.Embeddings;

namespace LupaFiscal.Tests;

/// <summary>Runs only when the real model is in data/models (never in CI, which does not download it).</summary>
public sealed class RealModelFactAttribute : FactAttribute
{
    public RealModelFactAttribute()
    {
        if (RealModel.Store is null) Skip = "Real model not found in data/models; run: model download";
    }
}

internal static class RealModel
{
    public static ModelStore? Store { get; } = Find();

    private static ModelStore? Find()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (!File.Exists(Path.Combine(directory.FullName, "LupaFiscal.slnx"))) continue;
            var store = new ModelStore(Path.Combine(directory.FullName, "data", "models"), ModelSpec.MultilingualE5Small);
            return store.AllFilesPresent ? store : null;
        }
        return null;
    }
}

public sealed class E5ModelTests
{
    private static readonly string[] Sentences =
    [
        "Posso deduzir as despesas de educação dos meus filhos no IRS?",
        "A mais-valia resultante da alienação de imóvel destinado a habitação própria e permanente é excluída de tributação.",
        "Os rendimentos prediais auferidos por não residentes são tributados à taxa especial do artigo 72.º do Código do IRS.",
        "O reinvestimento do valor de realização deve ocorrer no prazo de trinta e seis meses, conforme o n.º 5.",
    ];

    private static readonly Lazy<E5Embedder> Embedder = new(() => E5Embedder.Load(RealModel.Store!));

    private static readonly Lazy<(Dictionary<string, int> Ids, List<double> Scores)> Vocabulary = new(LoadVocabulary);

    private static (Dictionary<string, int>, List<double>) LoadVocabulary()
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(RealModel.Store!.PathOf(ModelSpec.VocabularyJsonFileName)));
        var ids = new Dictionary<string, int>(StringComparer.Ordinal);
        var scores = new List<double>();
        foreach (var entry in document.RootElement.GetProperty("model").GetProperty("vocab").EnumerateArray())
        {
            ids.TryAdd(entry[0].GetString()!, scores.Count);
            scores.Add(entry[1].GetDouble());
        }
        return (ids, scores);
    }

    [RealModelFact]
    public void PieceIdsMatchTheModelVocabularyInTokenizerJson()
    {
        var (ids, _) = Vocabulary.Value;
        Assert.Equal(250_002, ids.Count);
        Assert.Equal((0, 1, 2, 3), (ids["<s>"], ids["<pad>"], ids["</s>"], ids["<unk>"]));

        foreach (var sentence in Sentences)
        {
            var pieces = Embedder.Value.Encoder.Pieces(sentence);
            Assert.True(pieces.Count > 5);
            Assert.All(pieces, p => Assert.Equal(ids[p.Piece], p.Id));
            Assert.DoesNotContain(pieces, p => p.Id == XlmRobertaPieceEncoder.UnknownId);
        }
    }

    [RealModelFact]
    public void EncodedIdsMatchAUnigramViterbiOverTokenizerJson()
    {
        foreach (var sentence in Sentences)
        {
            var encoded = Embedder.Value.Encoder.Encode(sentence, 512);

            Assert.Equal(XlmRobertaPieceEncoder.BeginId, encoded[0]);
            Assert.Equal(XlmRobertaPieceEncoder.EndId, encoded[^1]);
            Assert.Equal(ReferenceIds(sentence), encoded[1..^1].Select(id => (int)id));
        }
    }

    [RealModelFact]
    public void KnownIdsForAPortugueseQuestion()
    {
        // Ids looked up by hand in tokenizer.json for "▁Posso ▁de du zir ▁as ▁despe sas ▁de ▁educação".
        var encoded = Embedder.Value.Encoder.Encode("Posso deduzir as despesas de educação", 512);

        Assert.Equal([0L, 183998, 8, 693, 26679, 237, 45483, 15796, 8, 81899, 2], encoded);
    }

    [RealModelFact]
    public void LongInputIsTruncatedToTheModelWindow()
    {
        var text = string.Join(" ", Enumerable.Repeat("rendimentos da categoria B", 400));

        Assert.Equal(512, Embedder.Value.Encoder.Encode(text, 512).Length);
        Assert.Equal(384, Embedder.Value.EmbedPassages([text], CancellationToken.None)[0].Length);
    }

    [RealModelFact]
    public void AParaphraseScoresHigherThanAnUnrelatedSentence()
    {
        var embedder = Embedder.Value;
        var query = embedder.EmbedQuery("Posso deduzir as despesas de educação dos meus filhos no IRS?");
        var passages = embedder.EmbedPassages(
        [
            "As propinas e o material escolar dos dependentes são dedutíveis à coleta do imposto.",
            "A receita de bacalhau com natas leva batatas, cebola e azeite.",
        ], CancellationToken.None);

        Assert.Equal(384, query.Length);
        Assert.All(passages.Append(query), v => Assert.Equal(1f, TensorPrimitives.Norm(v), 3));
        var paraphrase = TensorPrimitives.Dot(query, passages[0]);
        var unrelated = TensorPrimitives.Dot(query, passages[1]);
        Assert.True(paraphrase > unrelated, $"paraphrase {paraphrase:0.000} vs unrelated {unrelated:0.000}");

        // Batching and padding do not change a vector.
        var alone = embedder.EmbedPassages(["A receita de bacalhau com natas leva batatas, cebola e azeite."], CancellationToken.None)[0];
        Assert.Equal(1f, TensorPrimitives.Dot(alone, passages[1]), 3);
    }

    [RealModelFact]
    public void PassageTokenCountIncludesPrefixAndSpecialTokens()
    {
        var embedder = Embedder.Value;
        const string Text = "Rendimentos da categoria B.";

        Assert.Equal(Embedder.Value.Encoder.Encode(E5Embedder.PassagePrefix + Text, 512).Length, embedder.CountPassageTokens(Text));
    }

    /// <summary>
    /// Independent reference: NFKC, a "▁" before every word (Metaspace), then the highest-scoring
    /// segmentation of each word over the Unigram pieces and scores in tokenizer.json.
    /// </summary>
    private static List<int> ReferenceIds(string text)
    {
        var (ids, scores) = Vocabulary.Value;
        var maxPiece = ids.Keys.Max(k => k.Length);
        var result = new List<int>();
        foreach (var word in text.Normalize(NormalizationForm.FormKC).Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var input = "▁" + word;
            var best = new double[input.Length + 1];
            var from = new (int Start, int Id)[input.Length + 1];
            Array.Fill(best, double.NegativeInfinity);
            best[0] = 0;
            for (var end = 1; end <= input.Length; end++)
            {
                for (var start = Math.Max(0, end - maxPiece); start < end; start++)
                {
                    if (double.IsNegativeInfinity(best[start])) continue;
                    if (!ids.TryGetValue(input[start..end], out var id) || id < 4) continue;
                    var score = best[start] + scores[id];
                    if (score > best[end])
                    {
                        best[end] = score;
                        from[end] = (start, id);
                    }
                }
            }
            Assert.False(double.IsNegativeInfinity(best[input.Length]), $"no segmentation for {input}");
            var pieces = new List<int>();
            for (var position = input.Length; position > 0; position = from[position].Start) pieces.Add(from[position].Id);
            pieces.Reverse();
            result.AddRange(pieces);
        }
        return result;
    }
}
