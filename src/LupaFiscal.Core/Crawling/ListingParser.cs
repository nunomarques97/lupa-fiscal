using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LupaFiscal.Core.Crawling;

/// <summary>One row of the rulings listing, mapped as described in docs/research/gate0.md ("Field mapping").</summary>
public sealed record ListingEntry(
    string Id,
    string FileName,
    string SourceUrl,
    string Tax,
    string Diploma,
    string Article,
    string Paragraph,
    DateOnly? PublishedOn,
    string ProcessNumber,
    string Subject);

public sealed record ListingParseResult(IReadOnlyList<ListingEntry> Entries, IReadOnlyList<string> Rejected);

/// <summary>
/// Parses the SharePoint "listdocs" JSON response: {"data": [[...], ...], "total": 0}, one row per
/// entry with one column per requested field, in the order of <see cref="TaxSource.Fields"/>.
/// Columns are mapped by field name through <see cref="TaxSource.Columns"/>, so libraries with other
/// field names or orders share one parser. The "total" property is unreliable and ignored.
/// </summary>
public static partial class ListingParser
{
    public static ListingParseResult Parse(string json, TaxSource source, UrlPolicy policy)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object
            || !document.RootElement.TryGetProperty("data", out var data)
            || data.ValueKind != JsonValueKind.Array)
        {
            throw new FormatException("Listing response has no \"data\" array.");
        }

        var columns = source.Columns;
        var fields = source.Fields;
        int Column(string name) => IndexOf(fields, name);
        var docIcon = Column(TaxSource.DocIconField);
        var number = Column(columns.ProcessNumber);
        var published = Column(columns.PublishedOn);
        var diploma = columns.Diploma is { } diplomaField ? Column(diplomaField) : -1;
        var articles = columns.Article.Select(Column).ToArray();
        var paragraph = columns.Paragraph is { } paragraphField ? Column(paragraphField) : -1;
        var subject = Column(columns.Subject);

        var entries = new List<ListingEntry>();
        var rejected = new List<string>();
        var rowNumber = 0;
        foreach (var row in data.EnumerateArray())
        {
            rowNumber++;
            if (row.ValueKind != JsonValueKind.Array || row.GetArrayLength() != fields.Count)
            {
                rejected.Add($"row {rowNumber}: unexpected shape");
                continue;
            }

            var cells = row.EnumerateArray().Select(cell => cell.ValueKind == JsonValueKind.String ? cell.GetString() ?? "" : "").ToArray();
            var href = HrefPattern().Match(cells[docIcon]) is { Success: true } match ? WebUtility.HtmlDecode(match.Groups[1].Value) : null;
            if (string.IsNullOrWhiteSpace(href))
            {
                rejected.Add($"row {rowNumber}: no PDF link");
                continue;
            }
            if (!Uri.TryCreate(source.Origin, href.Trim(), out var url))
            {
                rejected.Add($"row {rowNumber}: invalid PDF link");
                continue;
            }
            if (!policy.IsAllowed(url))
            {
                var why = url.Scheme != Uri.UriSchemeHttps ? $"uses {url.Scheme}, not https" : "leaves the allowlisted host";
                rejected.Add($"row {rowNumber}: PDF link {why}, outside the https host allowlist ({UrlPolicy.Describe(url)})");
                continue;
            }
            if (!url.AbsolutePath.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
            {
                rejected.Add($"row {rowNumber}: link is not a PDF ({UrlPolicy.Describe(url)})");
                continue;
            }

            var fileName = Uri.UnescapeDataString(url.Segments[^1]);
            entries.Add(new ListingEntry(
                Id: source.IdPrefix + RulingId.FromFileName(fileName),
                FileName: fileName,
                SourceUrl: url.AbsoluteUri,
                Tax: source.Code,
                // Libraries without a diploma column (RITI, CESE, CSB) concern their own code only.
                Diploma: diploma >= 0 ? Text(cells[diploma]) : source.Code,
                Article: NormalizeArticle(articles.Select(i => Text(cells[i])).FirstOrDefault(a => a.Length > 0) ?? ""),
                Paragraph: paragraph >= 0 ? Text(cells[paragraph]) : "",
                PublishedOn: ParseDate(Text(cells[published])),
                ProcessNumber: Text(cells[number]),
                Subject: Text(cells[subject])));
        }

        return new ListingParseResult(Disambiguate(entries), rejected);
    }

    /// <summary>"010" becomes "10", "012-B" becomes "12-B"; anything without leading digits is kept as is.</summary>
    public static string NormalizeArticle(string article)
    {
        var match = ArticlePattern().Match(article);
        return match.Success ? match.Groups[1].Value + match.Groups[2].Value.Trim() : article;
    }

    private static int IndexOf(IReadOnlyList<string> fields, string name)
    {
        for (var i = 0; i < fields.Count; i++)
        {
            if (fields[i] == name) return i;
        }
        throw new InvalidOperationException($"Listing field {name} is not requested.");
    }

    private static DateOnly? ParseDate(string value) =>
        DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : null;

    private static string Text(string html) =>
        WhiteSpace().Replace(WebUtility.HtmlDecode(TagPattern().Replace(html, " ")), " ").Trim();

    // The same PDF listed twice is one ruling; different file names that sanitise to the same id
    // all get a hash suffix, so the id does not depend on listing order.
    private static List<ListingEntry> Disambiguate(List<ListingEntry> entries)
    {
        var unique = entries
            .GroupBy(entry => entry.SourceUrl, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToList();
        var clashing = unique
            .GroupBy(entry => entry.Id, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet(StringComparer.Ordinal);
        return unique
            .Select(entry => clashing.Contains(entry.Id)
                ? entry with { Id = RulingId.Disambiguate(entry.Id, entry.FileName) }
                : entry)
            .ToList();
    }

    [GeneratedRegex("""href\s*=\s*['"]([^'"]+)['"]""", RegexOptions.IgnoreCase)]
    private static partial Regex HrefPattern();

    [GeneratedRegex("<[^>]*>")]
    private static partial Regex TagPattern();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhiteSpace();

    [GeneratedRegex(@"^0*(\d+)(.*)$")]
    private static partial Regex ArticlePattern();
}
