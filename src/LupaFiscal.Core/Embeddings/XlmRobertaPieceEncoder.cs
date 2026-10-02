using Microsoft.ML.Tokenizers;

namespace LupaFiscal.Core.Embeddings;

/// <summary>
/// XLM-RoBERTa input ids from the model's SentencePiece Unigram file (Microsoft.ML.Tokenizers).
/// The SentencePiece ids are shifted to the fairseq vocabulary the model was trained with:
/// &lt;s&gt;=0, &lt;pad&gt;=1, &lt;/s&gt;=2, &lt;unk&gt;=3, and every other piece is its SentencePiece id + 1.
/// </summary>
public sealed class XlmRobertaPieceEncoder
{
    public const int BeginId = 0;
    public const int PadId = 1;
    public const int EndId = 2;
    public const int UnknownId = 3;

    private readonly SentencePieceTokenizer _pieces;

    private XlmRobertaPieceEncoder(SentencePieceTokenizer pieces) => _pieces = pieces;

    public static XlmRobertaPieceEncoder Load(string sentencePieceModelPath)
    {
        using var stream = File.OpenRead(sentencePieceModelPath);
        return new XlmRobertaPieceEncoder(
            SentencePieceTokenizer.Create(stream, addBeginningOfSentence: false, addEndOfSentence: false));
    }

    /// <summary>Pieces of the text without special tokens.</summary>
    public int Count(string text) => _pieces.CountTokens(text);

    /// <summary>Model ids with &lt;s&gt; and &lt;/s&gt;, the text truncated so the whole fits in <paramref name="maxTokens"/>.</summary>
    public long[] Encode(string text, int maxTokens)
    {
        var ids = _pieces.EncodeToIds(text);
        var length = Math.Min(ids.Count, maxTokens - 2);
        var result = new long[length + 2];
        result[0] = BeginId;
        for (var i = 0; i < length; i++) result[i + 1] = ToModelId(ids[i]);
        result[^1] = EndId;
        return result;
    }

    /// <summary>Pieces with their model ids, without special tokens (used by the parity tests).</summary>
    public IReadOnlyList<(string Piece, int Id)> Pieces(string text) =>
        _pieces.EncodeToTokens(text, out _).Select(t => (t.Value, ToModelId(t.Id))).ToList();

    internal static int ToModelId(int sentencePieceId) => sentencePieceId switch
    {
        0 => UnknownId,
        1 => BeginId,
        2 => EndId,
        _ => sentencePieceId + 1,
    };
}
