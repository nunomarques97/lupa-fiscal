using System.Text;
using System.Text.Json;
using LupaFiscal.Core.Crawling;

namespace LupaFiscal.Core.Corpus;

/// <summary>
/// On-disk corpus cache for one tax: data/corpus/&lt;tax&gt;/ holds manifest.json (ruling metadata
/// and state), listing.json (last listing response), pdf/&lt;id&gt;.pdf, text/&lt;id&gt;.txt and
/// scanned-skipped.md. Every file name comes from a validated ruling id, and every path is
/// checked to stay inside the tax directory. Writes go to a temporary file first, so an
/// interrupted run never leaves a partial file under its final name.
/// </summary>
public sealed class CorpusStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public CorpusStore(string corpusRoot, TaxSource source)
    {
        Source = source;
        TaxDirectory = Path.GetFullPath(Path.Combine(corpusRoot, source.DirectoryName));
        PdfDirectory = Path.Combine(TaxDirectory, "pdf");
        TextDirectory = Path.Combine(TaxDirectory, "text");
    }

    public TaxSource Source { get; }

    public string TaxDirectory { get; }

    public string PdfDirectory { get; }

    public string TextDirectory { get; }

    public string ManifestPath => Path.Combine(TaxDirectory, "manifest.json");

    public string ListingPath => Path.Combine(TaxDirectory, "listing.json");

    public string ScannedReportPath => Path.Combine(TaxDirectory, "scanned-skipped.md");

    public string PdfPath(string id) => Contained(PdfDirectory, id, ".pdf");

    public string TextPath(string id) => Contained(TextDirectory, id, ".txt");

    public bool HasPdf(string id) => File.Exists(PdfPath(id));

    public byte[] ReadPdf(string id) => File.ReadAllBytes(PdfPath(id));

    public void WritePdf(string id, byte[] content) => WriteAtomic(PdfPath(id), content);

    public void WriteText(string id, string text) => WriteAtomic(TextPath(id), Encoding.UTF8.GetBytes(text));

    public void DeleteText(string id) => File.Delete(TextPath(id));

    public string? ReadListing() => File.Exists(ListingPath) ? File.ReadAllText(ListingPath, Encoding.UTF8) : null;

    public void WriteListing(string json) => WriteAtomic(ListingPath, Encoding.UTF8.GetBytes(json));

    public CorpusManifest? LoadManifest()
    {
        if (!File.Exists(ManifestPath)) return null;
        var manifest = JsonSerializer.Deserialize<CorpusManifest>(File.ReadAllBytes(ManifestPath), JsonOptions)
            ?? throw new InvalidDataException($"Empty corpus manifest: {ManifestPath}");
        if (manifest.Version != CorpusManifest.CurrentVersion)
        {
            throw new InvalidDataException($"Unsupported corpus manifest version {manifest.Version}: {ManifestPath}");
        }
        foreach (var ruling in manifest.Rulings)
        {
            if (!RulingId.IsValid(ruling.Id))
            {
                throw new InvalidDataException($"Corpus manifest contains an invalid ruling id: {ManifestPath}");
            }
        }
        return manifest;
    }

    public void SaveManifest(CorpusManifest manifest) =>
        WriteAtomic(ManifestPath, JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions));

    public void WriteScannedReport(IEnumerable<RulingRecord> rulings)
    {
        var skipped = rulings.Where(r => r.State == RulingState.ScannedSkipped).OrderBy(r => r.Id, StringComparer.Ordinal).ToList();
        var builder = new StringBuilder();
        builder.AppendLine($"# Scanned PDFs skipped ({Source.Code})");
        builder.AppendLine();
        builder.AppendLine($"PDFs with fewer than {Extraction.PdfTextExtractor.MinTextChars} letters or digits in their text layer are not indexed (no OCR in v0.1).");
        builder.AppendLine();
        builder.AppendLine($"Count: {skipped.Count}");
        builder.AppendLine();
        if (skipped.Count > 0)
        {
            builder.AppendLine("| Ruling id | Process | Pages | Text chars | Source |");
            builder.AppendLine("|---|---|---|---|---|");
            foreach (var r in skipped)
            {
                builder.AppendLine($"| {r.Id} | {r.ProcessNumber} | {r.PageCount} | {r.TextChars} | {r.SourceUrl} |");
            }
        }
        WriteAtomic(ScannedReportPath, Encoding.UTF8.GetBytes(builder.ToString()));
    }

    private static string Contained(string directory, string id, string extension)
    {
        if (!RulingId.IsValid(id))
        {
            throw new ArgumentException("Invalid ruling id for a cache file name.", nameof(id));
        }
        var path = Path.GetFullPath(Path.Combine(directory, id + extension));
        var root = Path.GetFullPath(directory) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Cache path escapes the corpus directory.", nameof(id));
        }
        return path;
    }

    private static void WriteAtomic(string path, byte[] content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".partial";
        File.WriteAllBytes(temporary, content);
        File.Move(temporary, path, overwrite: true);
    }
}
