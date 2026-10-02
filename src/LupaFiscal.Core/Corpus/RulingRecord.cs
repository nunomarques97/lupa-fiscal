using System.Text.Json.Serialization;

namespace LupaFiscal.Core.Corpus;

[JsonConverter(typeof(JsonStringEnumConverter<RulingState>))]
public enum RulingState
{
    Pending,
    Downloaded,
    Extracted,
    ScannedSkipped,
    Failed,
}

/// <summary>A listed ruling, its metadata and its crawl state. Persisted in the corpus manifest.</summary>
public sealed class RulingRecord
{
    public required string Id { get; set; }

    /// <summary>Remote PDF file name, kept for reference only; never used as a local path.</summary>
    public required string FileName { get; set; }

    /// <summary>Official PDF URL (https, allowlisted host, percent-encoded).</summary>
    public required string SourceUrl { get; set; }

    public required string Tax { get; set; }

    public string Diploma { get; set; } = "";

    public string Article { get; set; } = "";

    public string Paragraph { get; set; } = "";

    /// <summary>Publication date from the listing.</summary>
    public DateOnly? PublishedOn { get; set; }

    /// <summary>Decision date ("com despacho de ...") from the PDF text, when present.</summary>
    public DateOnly? DecisionDate { get; set; }

    public string ProcessNumber { get; set; } = "";

    /// <summary>True when the listing had no process number and it was taken from the PDF text.</summary>
    public bool ProcessNumberFromPdf { get; set; }

    public string Subject { get; set; } = "";

    public RulingState State { get; set; } = RulingState.Pending;

    /// <summary>Why the ruling failed or was skipped.</summary>
    public string? Reason { get; set; }

    /// <summary>False when the ruling no longer appears in the latest listing.</summary>
    public bool Listed { get; set; } = true;

    public long? PdfBytes { get; set; }

    public string? PdfSha256 { get; set; }

    public int? PageCount { get; set; }

    /// <summary>Letters and digits in the extracted text layer.</summary>
    public int? TextChars { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class CorpusManifest
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;

    public required string Tax { get; set; }

    public DateTimeOffset? ListingFetchedAt { get; set; }

    public List<RulingRecord> Rulings { get; set; } = [];
}
