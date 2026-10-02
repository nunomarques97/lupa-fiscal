using System.Numerics.Tensors;
using Microsoft.ML.OnnxRuntime;

namespace LupaFiscal.Core.Embeddings;

/// <summary>
/// multilingual-e5-small on ONNX Runtime (CPU). Inputs get the "query: " or "passage: " prefix the
/// model was trained with; the output is the attention-masked mean of the last hidden state,
/// L2-normalised, so a dot product is the cosine similarity.
/// </summary>
public sealed class E5Embedder : IEmbedder
{
    public const string QueryPrefix = "query: ";
    public const string PassagePrefix = "passage: ";

    /// <summary>Output size of multilingual-e5-small; checked against the loaded model.</summary>
    public const int ModelDimensions = 384;
    private const int BatchSize = 16;

    private readonly InferenceSession _session;
    private readonly XlmRobertaPieceEncoder _encoder;
    private readonly bool _needsTokenTypes;
    private readonly string _outputName;
    private readonly int _passageOverhead;

    private E5Embedder(InferenceSession session, XlmRobertaPieceEncoder encoder, string modelId)
    {
        _session = session;
        _encoder = encoder;
        ModelId = modelId;
        _needsTokenTypes = session.InputMetadata.ContainsKey("token_type_ids");
        _outputName = session.OutputMetadata.ContainsKey("last_hidden_state")
            ? "last_hidden_state"
            : session.OutputMetadata.Keys.First();
        Dimensions = (int)session.OutputMetadata[_outputName].Dimensions[^1];
        if (Dimensions != ModelDimensions)
        {
            throw new ModelIntegrityException($"Model output has {Dimensions} dimensions, expected {ModelDimensions}.");
        }
        // Pieces never span a space, so "passage: " + text counts as the prefix pieces plus the
        // text pieces, plus <s> and </s>.
        _passageOverhead = encoder.Count(PassagePrefix.TrimEnd()) + 2;
    }

    /// <summary>Verifies every pinned file's SHA-256, then loads the model. Nothing is loaded if a check fails.</summary>
    public static E5Embedder Load(ModelStore store)
    {
        store.VerifyAll();
        var encoder = XlmRobertaPieceEncoder.Load(store.PathOf(ModelSpec.SentencePieceFileName));
        var options = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
        try
        {
            var session = new InferenceSession(store.PathOf(ModelSpec.ModelFileName), options);
            try
            {
                return new E5Embedder(session, encoder, store.Spec.Id);
            }
            catch
            {
                session.Dispose();
                throw;
            }
        }
        finally
        {
            options.Dispose();
        }
    }

    public string ModelId { get; }

    public int Dimensions { get; }

    public int MaxTokens => 512;

    public XlmRobertaPieceEncoder Encoder => _encoder;

    public int CountTokens(string text) => _encoder.Count(text);

    public int CountPassageTokens(string text) => _passageOverhead + _encoder.Count(text);

    public float[] EmbedQuery(string query) => Embed([QueryPrefix + query])[0];

    public IReadOnlyList<float[]> EmbedPassages(IReadOnlyList<string> passages, CancellationToken cancellationToken)
    {
        var result = new float[passages.Count][];
        // Batches of similar length waste little work on padding.
        var order = Enumerable.Range(0, passages.Count).OrderBy(i => passages[i].Length).ToArray();
        for (var start = 0; start < order.Length; start += BatchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var batch = order.Skip(start).Take(BatchSize).ToArray();
            var vectors = Embed(batch.Select(i => PassagePrefix + passages[i]).ToList());
            for (var j = 0; j < batch.Length; j++) result[batch[j]] = vectors[j];
        }
        return result;
    }

    private float[][] Embed(IReadOnlyList<string> inputs)
    {
        var encoded = inputs.Select(text => _encoder.Encode(text, MaxTokens)).ToArray();
        var batch = encoded.Length;
        var length = encoded.Max(e => e.Length);
        var ids = new long[batch * length];
        var mask = new long[batch * length];
        for (var b = 0; b < batch; b++)
        {
            var row = b * length;
            Array.Fill(ids, XlmRobertaPieceEncoder.PadId, row, length);
            encoded[b].CopyTo(ids, row);
            Array.Fill(mask, 1L, row, encoded[b].Length);
        }

        long[] shape = [batch, length];
        var names = new List<string> { "input_ids", "attention_mask" };
        var values = new List<OrtValue>
        {
            OrtValue.CreateTensorValueFromMemory(ids, shape),
            OrtValue.CreateTensorValueFromMemory(mask, shape),
        };
        if (_needsTokenTypes)
        {
            names.Add("token_type_ids");
            values.Add(OrtValue.CreateTensorValueFromMemory(new long[batch * length], shape));
        }

        try
        {
            using var runOptions = new RunOptions();
            using var outputs = _session.Run(runOptions, names, values, [_outputName]);
            var hidden = outputs[0].GetTensorDataAsSpan<float>();
            var dims = Dimensions;
            var vectors = new float[batch][];
            for (var b = 0; b < batch; b++)
            {
                var sum = new float[dims];
                var tokens = encoded[b].Length;
                for (var t = 0; t < tokens; t++)
                {
                    TensorPrimitives.Add(sum, hidden.Slice((b * length + t) * dims, dims), sum);
                }
                TensorPrimitives.Divide(sum, tokens, sum);
                vectors[b] = VectorMath.Normalize(sum);
            }
            return vectors;
        }
        finally
        {
            foreach (var value in values) value.Dispose();
        }
    }

    public void Dispose() => _session.Dispose();
}
