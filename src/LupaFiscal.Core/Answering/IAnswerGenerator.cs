namespace LupaFiscal.Core.Answering;

/// <summary>Why generation ended.</summary>
public enum GenerationStop
{
    /// <summary>The model emitted its end-of-text token.</summary>
    EndOfText,

    /// <summary>The max-new-tokens limit was reached; the last sentence may be cut.</summary>
    TokenLimit,

    /// <summary>The time limit was reached; the last sentence may be cut.</summary>
    TimeLimit,
}

/// <summary>One generation: a system message, a user message and its limits.</summary>
public sealed record GenerationRequest(string System, string User, int MaxNewTokens, TimeSpan TimeLimit);

/// <summary>The raw model output (never shown before <see cref="CitationEnforcer"/> checks it) and its timings.</summary>
public sealed record GenerationOutput(
    string Text,
    int PromptTokens,
    int GeneratedTokens,
    double FirstTokenMs,
    double TotalMs,
    GenerationStop Stop);

/// <summary>
/// Generates text from a local model in process. The real implementation is <see cref="OnnxGenAiGenerator"/>;
/// tests use a fake behind this interface, so no model is needed to test the pipeline.
/// </summary>
public interface IAnswerGenerator : IDisposable
{
    /// <summary>Model and revision, reported with every answer.</summary>
    string ModelId { get; }

    /// <summary>Longest sequence the model accepts, prompt and generated tokens included.</summary>
    int ContextTokens { get; }

    /// <summary>Model tokens of the text alone (no chat template).</summary>
    int CountTokens(string text);

    /// <summary>
    /// Runs one generation. Stops at the end-of-text token, at <see cref="GenerationRequest.MaxNewTokens"/> or at
    /// <see cref="GenerationRequest.TimeLimit"/>, returning what was generated so far.
    /// </summary>
    /// <exception cref="OperationCanceledException">The cancellation token was cancelled.</exception>
    GenerationOutput Generate(GenerationRequest request, CancellationToken cancellationToken);
}
