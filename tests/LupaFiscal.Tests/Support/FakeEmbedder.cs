using System.Text;
using System.Text.RegularExpressions;
using LupaFiscal.Core.Embeddings;
using LupaFiscal.Core.Search;

namespace LupaFiscal.Tests.Support;

/// <summary>
/// Deterministic stand-in for the real model: one "token" per word or punctuation mark, and a
/// bag-of-words vector (folded words hashed into a few dimensions), L2-normalised. Texts sharing
/// words are close, which is enough to exercise vector search, fusion and filters offline.
/// </summary>
internal sealed partial class FakeEmbedder(int dimensions = 64) : IEmbedder
{
    public const string FakeModelId = "fake/bag-of-words@1";

    public string ModelId => FakeModelId;

    public int Dimensions => dimensions;

    public int MaxTokens => 512;

    public int PassagesEmbedded { get; private set; }

    public int QueriesEmbedded { get; private set; }

    public int CountTokens(string text) => Piece().Count(text);

    public int CountPassageTokens(string text) => CountTokens("passage:") + CountTokens(text) + 2;

    public float[] EmbedQuery(string query)
    {
        QueriesEmbedded++;
        return Embed(query);
    }

    public IReadOnlyList<float[]> EmbedPassages(IReadOnlyList<string> passages, CancellationToken cancellationToken)
    {
        PassagesEmbedded += passages.Count;
        return passages.Select(Embed).ToList();
    }

    private float[] Embed(string text)
    {
        var vector = new float[dimensions];
        foreach (Match word in Word().Matches(text))
        {
            var folded = QueryTerms.Fold(word.Value);
            if (folded.Length < 3) continue;
            vector[(int)(Fnv1a(folded) % (uint)dimensions)] += 1;
        }
        return VectorMath.Normalize(vector);
    }

    private static uint Fnv1a(string text)
    {
        var hash = 2166136261u;
        foreach (var b in Encoding.UTF8.GetBytes(text))
        {
            hash ^= b;
            hash *= 16777619u;
        }
        return hash;
    }

    public void Dispose()
    {
    }

    [GeneratedRegex(@"\w+|[^\w\s]")]
    private static partial Regex Piece();

    [GeneratedRegex(@"\w+")]
    private static partial Regex Word();
}
