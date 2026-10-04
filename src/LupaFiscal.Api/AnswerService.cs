using System.Diagnostics;
using LupaFiscal.Core.Answering;
using LupaFiscal.Core.Embeddings;
using LupaFiscal.Core.Search;

namespace LupaFiscal.Api;

/// <summary>The loaded answer model, or null while it loads or when it is absent or failed to load.</summary>
public interface IAnswerGeneratorProvider
{
    IAnswerGenerator? Generator { get; }
}

/// <summary>
/// Loads <see cref="AnswerModel.Default"/> from data/models in the background once the host starts, so
/// startup and search never wait for it (checking the SHA-256 of its files alone takes seconds). Any
/// failure leaves the API without answers, never without search.
/// </summary>
public sealed class AnswerModelLoader(string modelsRoot, AnswerModel model, ILogger<AnswerModelLoader> logger)
    : IAnswerGeneratorProvider, IHostedService, IDisposable
{
    private readonly Lock _gate = new();
    private volatile IAnswerGenerator? _generator;
    private bool _disposed;

    public IAnswerGenerator? Generator => _generator;

    /// <summary>Completes when loading has ended, whatever its outcome.</summary>
    public Task Completion { get; private set; } = Task.CompletedTask;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Not awaited: the host starts serving while the model loads.
        Completion = Task.Factory.StartNew(Load, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private void Load()
    {
        var store = new ModelStore(modelsRoot, model.Spec);
        if (!store.AllFilesPresent)
        {
            logger.LogInformation("Answer model {Model} not found in {Directory}; /api/answer is unavailable. Run: model download --model {Model}",
                model.Name, store.DirectoryPath, model.Name);
            return;
        }
        try
        {
            var stopwatch = Stopwatch.StartNew();
            var generator = OnnxGenAiGenerator.Load(store, model);
            lock (_gate)
            {
                if (_disposed)
                {
                    generator.Dispose();
                    return;
                }
                _generator = generator;
            }
            logger.LogInformation("Answer model {Model} loaded in {Seconds:0.0} s.", model.Name, stopwatch.Elapsed.TotalSeconds);
        }
        catch (Exception ex)
        {
            logger.LogWarning("Answer model {Model} was not loaded; /api/answer is unavailable. {Reason}", model.Name, ex.Message);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _generator?.Dispose();
            _generator = null;
        }
    }
}

public sealed record AnswerServiceOptions
{
    /// <summary>How long a request waits for the running generation to end before it is refused as busy.</summary>
    public TimeSpan QueueWait { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Prompt bounds, max new tokens and the generation time limit.</summary>
    public AnswerOptions Answer { get; init; } = new();
}

public enum AnswerOutcomeKind
{
    Answered,
    Unavailable,
    Busy,
}

public sealed record AnswerOutcome(AnswerOutcomeKind Kind, AnswerResponse? Response)
{
    public static AnswerOutcome Unavailable { get; } = new(AnswerOutcomeKind.Unavailable, null);

    public static AnswerOutcome Busy { get; } = new(AnswerOutcomeKind.Busy, null);
}

/// <summary>
/// Answers one question at a time with the loaded model, from the same hybrid search as /api/search.
/// A request waits at most <see cref="AnswerServiceOptions.QueueWait"/> for the slot; the slot is released
/// only when the generation has actually stopped, so an aborted request never lets two generations overlap.
/// </summary>
public sealed class AnswerService(SearchIndex index, IAnswerGeneratorProvider models, AnswerServiceOptions options)
{
    private readonly SemaphoreSlim _slot = new(1, 1);

    /// <exception cref="OperationCanceledException">The cancellation token was cancelled (the client went away).</exception>
    public async Task<AnswerOutcome> AnswerAsync(SearchQuery query, CancellationToken cancellationToken)
    {
        if (models.Generator is not { } generator) return AnswerOutcome.Unavailable;
        if (!await _slot.WaitAsync(options.QueueWait, cancellationToken)) return AnswerOutcome.Busy;
        try
        {
            var answerer = new Answerer(index.Searcher, generator, options.Answer);
            var filters = new SearchFilters(query.Tax, query.Article, query.Year);
            // Generation is CPU-bound and synchronous: it runs on its own thread and is awaited to its end,
            // also when cancelled, before the slot is released.
            var result = await Task.Factory.StartNew(() => answerer.Answer(query.Text, filters, cancellationToken),
                CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            return new AnswerOutcome(AnswerOutcomeKind.Answered, ToResponse(query.Text, result));
        }
        finally
        {
            _slot.Release();
        }
    }

    /// <summary>
    /// The response: only citations whose source URL is https on the official host, and only sentences
    /// that keep at least one of them; when no sentence remains, the fixed refusal.
    /// </summary>
    internal static AnswerResponse ToResponse(string query, AnswerResult result)
    {
        var citations = new List<AnswerCitationItem>();
        foreach (var citation in result.Citations)
        {
            var hit = citation.Hit;
            if (SearchIndex.OfficialSourceUrl(hit.SourceUrl) is not { } sourceUrl) continue;
            citations.Add(new AnswerCitationItem(citation.Id, hit.RulingId, hit.ProcessNumber, hit.Tax, hit.Article, hit.Date,
                hit.Subject, hit.Section, hit.Passage, sourceUrl));
        }
        var available = citations.Select(c => c.Id).ToHashSet();
        var sentences = result.Status == AnswerStatus.Answered
            ? result.Sentences
                .Select(s => new AnswerSentenceItem(s.Text, s.Citations.Where(available.Contains).ToList()))
                .Where(s => s.Citations.Count > 0)
                .ToList()
            : [];

        var firstTokenMs = Math.Round(result.FirstTokenMs, 1);
        var tookMs = Math.Round(result.TookMs, 1);
        if (sentences.Count == 0)
        {
            return new AnswerResponse(query, AnswerResponse.Refused, [new AnswerSentenceItem(CitationEnforcer.RefusalText, [])], [],
                result.ModelId, firstTokenMs, tookMs);
        }
        var cited = sentences.SelectMany(s => s.Citations).ToHashSet();
        return new AnswerResponse(query, AnswerResponse.Answered, sentences, citations.Where(c => cited.Contains(c.Id)).ToList(),
            result.ModelId, firstTokenMs, tookMs);
    }
}
