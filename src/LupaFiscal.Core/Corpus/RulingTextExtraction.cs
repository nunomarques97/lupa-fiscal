using LupaFiscal.Core.Extraction;

namespace LupaFiscal.Core.Corpus;

public sealed record ReextractSummary(CorpusCounts Counts, int Processed);

/// <summary>
/// Turns a cached ruling PDF into its text file and updates the ruling's state: extracted, or
/// scanned-skipped when the PDF has no usable text layer, or failed when the PDF cannot be parsed.
/// Works offline on the corpus cache.
/// </summary>
public sealed class RulingTextExtraction(
    CorpusStore store,
    PdfTextExtractor extractor,
    TextWriter log,
    Func<DateTimeOffset>? now = null)
{
    private readonly Func<DateTimeOffset> _now = now ?? (() => DateTimeOffset.UtcNow);

    public void Extract(RulingRecord ruling)
    {
        try
        {
            var result = extractor.Extract(store.ReadPdf(ruling.Id));
            ruling.PageCount = result.PageCount;
            ruling.TextChars = result.TextChars;
            ruling.UpdatedAt = _now();
            if (!result.HasTextLayer)
            {
                store.DeleteText(ruling.Id);
                ruling.State = RulingState.ScannedSkipped;
                ruling.Reason = $"text layer has {result.TextChars} letters or digits, below {PdfTextExtractor.MinTextChars}";
                log.WriteLine($"{ruling.Id}: scanned PDF skipped ({ruling.Reason}).");
                return;
            }

            store.WriteText(ruling.Id, result.Text);
            if (ruling.ProcessNumber.Length == 0 || ruling.ProcessNumberFromPdf)
            {
                ruling.ProcessNumber = result.ProcessNumber ?? "";
                ruling.ProcessNumberFromPdf = result.ProcessNumber is not null;
            }
            ruling.DecisionDate = result.DecisionDate;
            ruling.State = RulingState.Extracted;
            ruling.Reason = null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not IOException and not UnauthorizedAccessException)
        {
            // PdfPig throws assorted exception types on malformed PDFs.
            ruling.State = RulingState.Failed;
            ruling.Reason = $"text extraction failed ({ex.GetType().Name})";
            ruling.UpdatedAt = _now();
            log.WriteLine($"{ruling.Id}: failed, {ruling.Reason}.");
        }
    }

    /// <summary>Extracts every listed ruling whose PDF is cached again, for example after a parser change. No network.</summary>
    public ReextractSummary ReextractAll(CancellationToken cancellationToken)
    {
        var manifest = store.LoadManifest()
            ?? throw new InvalidOperationException($"No corpus manifest at {store.ManifestPath}.");
        var processed = 0;
        try
        {
            foreach (var ruling in manifest.Rulings.Where(r => r.Listed && store.HasPdf(r.Id)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (ruling.State is RulingState.Pending or RulingState.Failed) ruling.State = RulingState.Downloaded;
                Extract(ruling);
                processed++;
            }
        }
        finally
        {
            store.SaveManifest(manifest);
            store.WriteScannedReport(manifest.Rulings.Where(r => r.Listed));
        }
        return new ReextractSummary(CorpusCounts.From(manifest), processed);
    }
}
