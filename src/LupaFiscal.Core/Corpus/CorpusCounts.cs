namespace LupaFiscal.Core.Corpus;

/// <summary>Ruling counts by state, over the rulings in the latest listing.</summary>
public sealed record CorpusCounts(
    int Listed,
    int Pending,
    int Downloaded,
    int Extracted,
    int ScannedSkipped,
    int Failed,
    int Delisted)
{
    /// <summary>
    /// True when no listed ruling still needs work: nothing pending and nothing downloaded but
    /// not yet extracted. Failed and scanned-skipped rulings are final states.
    /// </summary>
    public bool IsComplete => Listed > 0 && Pending == 0 && Downloaded == 0;

    /// <summary>PDFs obtained so far (extracted, skipped as scanned, or awaiting extraction).</summary>
    public int PdfsCached => Downloaded + Extracted + ScannedSkipped;

    public static CorpusCounts Total(IEnumerable<CorpusCounts> counts) =>
        counts.Aggregate(new CorpusCounts(0, 0, 0, 0, 0, 0, 0), (total, c) => new CorpusCounts(
            total.Listed + c.Listed, total.Pending + c.Pending, total.Downloaded + c.Downloaded,
            total.Extracted + c.Extracted, total.ScannedSkipped + c.ScannedSkipped, total.Failed + c.Failed,
            total.Delisted + c.Delisted));

    public static CorpusCounts From(CorpusManifest manifest)
    {
        var listed = manifest.Rulings.Where(r => r.Listed).ToList();
        return new CorpusCounts(
            Listed: listed.Count,
            Pending: listed.Count(r => r.State == RulingState.Pending),
            Downloaded: listed.Count(r => r.State == RulingState.Downloaded),
            Extracted: listed.Count(r => r.State == RulingState.Extracted),
            ScannedSkipped: listed.Count(r => r.State == RulingState.ScannedSkipped),
            Failed: listed.Count(r => r.State == RulingState.Failed),
            Delisted: manifest.Rulings.Count - listed.Count);
    }
}
