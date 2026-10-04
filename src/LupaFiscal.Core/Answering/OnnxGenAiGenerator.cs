using System.Diagnostics;
using System.Text;
using LupaFiscal.Core.Embeddings;
using Microsoft.ML.OnnxRuntimeGenAI;

namespace LupaFiscal.Core.Answering;

/// <summary>
/// Runs a pinned <see cref="AnswerModel"/> in process with ONNX Runtime GenAI on the CPU: no service, no
/// network, no account. Every model file is checked against its SHA-256 before the model is loaded.
/// Decoding is greedy (no sampling), though multithreaded arithmetic can still change a token between runs.
/// One generation runs at a time.
/// </summary>
public sealed class OnnxGenAiGenerator : IAnswerGenerator
{
    // ONNX Runtime GenAI must be shut down once, after every model is released; the handle does that at exit.
    private static readonly Lazy<OgaHandle> Runtime = new(() =>
    {
        var handle = new OgaHandle();
        AppDomain.CurrentDomain.ProcessExit += (_, _) => handle.Dispose();
        return handle;
    });

    private readonly AnswerModel _model;
    private readonly Model _onnx;
    private readonly Tokenizer _tokenizer;
    private readonly Lock _gate = new();

    private OnnxGenAiGenerator(AnswerModel model, Model onnx, Tokenizer tokenizer)
    {
        _model = model;
        _onnx = onnx;
        _tokenizer = tokenizer;
    }

    public string ModelId => _model.Id;

    public int ContextTokens => _model.ContextTokens;

    /// <exception cref="ModelIntegrityException">A file is missing or does not match its pinned SHA-256; nothing was loaded.</exception>
    public static OnnxGenAiGenerator Load(ModelStore store, AnswerModel model)
    {
        if (store.Spec.Id != model.Spec.Id || store.Spec.DirectoryName != model.Spec.DirectoryName)
        {
            throw new ArgumentException("The model store does not hold this answer model.", nameof(store));
        }
        try
        {
            store.VerifyAll();
        }
        catch (ModelIntegrityException ex)
        {
            // The store's hint names the embedding download; an answer model is downloaded by name.
            throw new ModelIntegrityException($"{ex.Message} --model {model.Name}");
        }
        _ = Runtime.Value;
        var onnx = new Model(store.DirectoryPath);
        try
        {
            return new OnnxGenAiGenerator(model, onnx, new Tokenizer(onnx));
        }
        catch
        {
            onnx.Dispose();
            throw;
        }
    }

    public int CountTokens(string text)
    {
        using var sequences = _tokenizer.Encode(text);
        return sequences[0].Length;
    }

    public GenerationOutput Generate(GenerationRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var stopwatch = Stopwatch.StartNew();
            using var input = _tokenizer.Encode(Format(request));
            var promptTokens = input[0].Length;
            using var parameters = new GeneratorParams(_onnx);
            parameters.SetSearchOption("max_length", Math.Min(promptTokens + request.MaxNewTokens, _model.ContextTokens));
            parameters.SetSearchOption("do_sample", false);
            using var generator = new Generator(_onnx, parameters);
            using var stream = _tokenizer.CreateStream();

            // The time limit and the caller's token both stop the native session, also during the prompt pass.
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limit.CancelAfter(request.TimeLimit);
            using var registration = limit.Token.Register(() => generator.SetRuntimeOption("terminate_session", "1"));

            var text = new StringBuilder();
            var generated = 0;
            double firstTokenMs = 0;
            var stop = GenerationStop.EndOfText;
            try
            {
                generator.AppendTokenSequences(input);
                var length = generator.GetSequence(0).Length;
                while (!generator.IsDone())
                {
                    if (generated >= request.MaxNewTokens)
                    {
                        stop = GenerationStop.TokenLimit;
                        break;
                    }
                    limit.Token.ThrowIfCancellationRequested();
                    generator.GenerateNextToken();
                    var sequence = generator.GetSequence(0);
                    if (generated == 0) firstTokenMs = stopwatch.Elapsed.TotalMilliseconds;
                    // The end-of-text token is not appended: decoding the last position again would repeat the previous token.
                    if (sequence.Length == length) break;
                    length = sequence.Length;
                    text.Append(stream.Decode(sequence[^1]));
                    generated++;
                }
                if (stop == GenerationStop.EndOfText && generated >= request.MaxNewTokens) stop = GenerationStop.TokenLimit;
            }
            catch (Exception ex) when (limit.IsCancellationRequested && ex is OnnxRuntimeGenAIException or OperationCanceledException)
            {
                cancellationToken.ThrowIfCancellationRequested();
                stop = GenerationStop.TimeLimit;
            }
            return new GenerationOutput(text.ToString(), promptTokens, generated, firstTokenMs, stopwatch.Elapsed.TotalMilliseconds, stop);
        }
    }

    /// <summary>The two messages wrapped in the model's chat template, ending where the assistant's turn starts.</summary>
    internal static string Format(AnswerModel model, GenerationRequest request) => model.Format switch
    {
        ChatFormat.Phi => $"<|system|>{request.System}<|end|><|user|>{request.User}<|end|><|assistant|>",
        _ => $"<|im_start|>system\n{request.System}<|im_end|>\n<|im_start|>user\n{request.User}<|im_end|>\n<|im_start|>assistant\n",
    };

    private string Format(GenerationRequest request) => Format(_model, request);

    public void Dispose()
    {
        lock (_gate)
        {
            _tokenizer.Dispose();
            _onnx.Dispose();
        }
    }
}
