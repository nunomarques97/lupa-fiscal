using System.Diagnostics;
using LupaFiscal.Core.Search;

namespace LupaFiscal.Core.Answering;

public enum AnswerStatus
{
    Answered,
    Refused,
}

public sealed record AnswerOptions
{
    public AnswerPromptOptions Prompt { get; init; } = new();

    /// <summary>Generation stops after this long and keeps only the sentences finished by then.</summary>
    public TimeSpan TimeLimit { get; init; } = TimeSpan.FromSeconds(90);
}

/// <summary>
/// A validated answer. <see cref="Citations"/> holds every passage cited by a shown sentence (each id in
/// <see cref="Sentences"/> is in it); <see cref="Provided"/> holds every passage given to the model.
/// The raw output and its counts are kept for evaluation, never for display.
/// </summary>
public sealed record AnswerResult(
    string Query,
    AnswerStatus Status,
    IReadOnlyList<AnswerSentence> Sentences,
    IReadOnlyList<PromptCitation> Citations,
    IReadOnlyList<PromptCitation> Provided,
    string ModelId,
    int PromptTokens,
    int GeneratedTokens,
    GenerationStop? Stop,
    double SearchMs,
    double FirstTokenMs,
    double GenerationMs,
    double TookMs,
    string RawOutput,
    int RawSentences,
    int ValidRawSentences)
{
    /// <summary>The shown text: sentences with their markers, or the fixed refusal.</summary>
    public string Text => Status == AnswerStatus.Refused
        ? CitationEnforcer.RefusalText
        : string.Join(' ', Sentences.Select(s => s.Text + " " + string.Concat(s.Citations.Select(id => $"[{id}]"))));
}

/// <summary>
/// Answers a question from the passages hybrid search retrieves (the same search as the search command,
/// one passage per ruling): builds the cited prompt, generates, then enforces strict citations in code.
/// Without passages the model is not called and the answer is the refusal.
/// </summary>
public sealed class Answerer(HybridSearcher searcher, IAnswerGenerator generator, AnswerOptions? options = null)
{
    public AnswerOptions Options { get; } = options ?? new AnswerOptions();

    /// <exception cref="ArgumentException">The filters have an article without a tax.</exception>
    /// <exception cref="OperationCanceledException">The cancellation token was cancelled.</exception>
    public AnswerResult Answer(string question, SearchFilters filters, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var search = searcher.Search(question, filters, new SearchOptions { Limit = Options.Prompt.MaxPassages });
        var searchMs = stopwatch.Elapsed.TotalMilliseconds;
        cancellationToken.ThrowIfCancellationRequested();

        var prompt = AnswerPromptBuilder.Build(question, search.Hits, Options.Prompt, generator.CountTokens, generator.ContextTokens);
        if (prompt.Citations.Count == 0)
        {
            return new AnswerResult(question, AnswerStatus.Refused, [], [], [], generator.ModelId, 0, 0, null, searchMs, 0, 0,
                stopwatch.Elapsed.TotalMilliseconds, string.Empty, 0, 0);
        }

        var output = generator.Generate(new GenerationRequest(prompt.System, prompt.User, Options.Prompt.MaxNewTokens, Options.TimeLimit),
            cancellationToken);
        var provided = prompt.Citations.Select(c => c.Id).ToHashSet();
        var enforced = CitationEnforcer.Enforce(output.Text, provided, complete: output.Stop == GenerationStop.EndOfText,
            rejected: AnswerPromptBuilder.ExampleSentences);
        var cited = enforced.Sentences.SelectMany(s => s.Citations).ToHashSet();

        return new AnswerResult(
            question,
            enforced.Refused ? AnswerStatus.Refused : AnswerStatus.Answered,
            enforced.Sentences,
            prompt.Citations.Where(c => cited.Contains(c.Id)).ToList(),
            prompt.Citations,
            generator.ModelId,
            output.PromptTokens,
            output.GeneratedTokens,
            output.Stop,
            searchMs,
            output.FirstTokenMs,
            output.TotalMs,
            stopwatch.Elapsed.TotalMilliseconds,
            output.Text,
            enforced.RawSentences,
            enforced.ValidRawSentences);
    }
}
