namespace LupaFiscal.Core.Embeddings;

/// <summary>
/// Turns text into L2-normalised vectors for retrieval. The real implementation is
/// <see cref="E5Embedder"/>; tests use a deterministic fake behind this interface.
/// </summary>
public interface IEmbedder : IDisposable
{
    /// <summary>Model and revision; stored in the index so a model change forces re-embedding.</summary>
    string ModelId { get; }

    int Dimensions { get; }

    /// <summary>Longest input the model accepts, in model tokens, special tokens included.</summary>
    int MaxTokens { get; }

    /// <summary>Model tokens of the text alone: no prefix and no special tokens.</summary>
    int CountTokens(string text);

    /// <summary>Model tokens of the full passage input: prefix, text and special tokens.</summary>
    int CountPassageTokens(string text);

    float[] EmbedQuery(string query);

    IReadOnlyList<float[]> EmbedPassages(IReadOnlyList<string> passages, CancellationToken cancellationToken);
}
