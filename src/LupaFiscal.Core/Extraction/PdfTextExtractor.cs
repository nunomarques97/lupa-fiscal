using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace LupaFiscal.Core.Extraction;

public sealed record ExtractionResult(
    string Text,
    int PageCount,
    int TextChars,
    string? ProcessNumber,
    DateOnly? DecisionDate)
{
    /// <summary>False for scanned PDFs: the text layer has fewer than <see cref="PdfTextExtractor.MinTextChars"/> letters or digits.</summary>
    public bool HasTextLayer => TextChars >= PdfTextExtractor.MinTextChars;
}

/// <summary>
/// Extracts the text layer of a ruling PDF with PdfPig. There is no OCR: a PDF whose text layer
/// holds fewer than <see cref="MinTextChars"/> letters or digits is treated as scanned and skipped.
/// </summary>
public sealed partial class PdfTextExtractor
{
    /// <summary>
    /// Threshold for a usable text layer, counted over the whole document. A real ruling holds
    /// thousands of letters (the metadata header alone is a few hundred), while a scanned page
    /// yields none or a few stray characters.
    /// </summary>
    public const int MinTextChars = 200;

    public ExtractionResult Extract(byte[] pdf)
    {
        using var document = PdfDocument.Open(pdf, new ParsingOptions { UseLenientParsing = true });
        var builder = new StringBuilder();
        foreach (var page in document.GetPages())
        {
            var pageText = ContentOrderTextExtractor.GetText(page);
            if (builder.Length > 0) builder.Append("\n\n");
            builder.Append(Clean(pageText));
        }

        var text = builder.ToString().Trim();
        var chars = text.Count(char.IsLetterOrDigit);
        return new ExtractionResult(text, document.NumberOfPages, chars, FindProcessNumber(text), FindDecisionDate(text));
    }

    /// <summary>
    /// Prefers the "Processo: N, com despacho ..." line; older rulings also repeat the number in a
    /// page header followed by the page number ("Processo: 2184/03  1"), which is used only as a
    /// fallback, without the page number.
    /// </summary>
    internal static string? FindProcessNumber(string text)
    {
        string? fallback = null;
        foreach (Match match in ProcessPattern().Matches(text))
        {
            var raw = PageNumberSuffix().Replace(match.Groups["number"].Value, "");
            var value = WhiteSpace().Replace(raw, " ").Trim().TrimEnd('.', ',', ';');
            if (value.Length == 0) continue;
            if (match.Groups["decision"].Success) return value;
            fallback ??= value;
        }
        return fallback;
    }

    internal static DateOnly? FindDecisionDate(string text)
    {
        var match = DecisionDatePattern().Match(text);
        if (!match.Success) return null;
        string[] formats = ["yyyy-MM-dd", "dd-MM-yyyy", "yyyy/MM/dd", "dd/MM/yyyy", "dd.MM.yyyy"];
        return DateOnly.TryParseExact(match.Groups[1].Value, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : null;
    }

    // Older rulings use no-break spaces between words and soft hyphens where a hyphen is printed
    // ("Subdirectora­Geral"); both become their plain equivalents so search and parsing see words.
    internal static string Clean(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var c in text.Replace("\r\n", "\n"))
        {
            builder.Append(c switch
            {
                '\r' => '\n',
                '­' => '-',
                '\0' => ' ',
                _ when c != '\n' && char.IsWhiteSpace(c) => ' ',
                _ => c,
            });
        }
        var lines = builder.ToString().Split('\n');
        return string.Join('\n', lines.Select(line => line.TrimEnd()));
    }

    // "Processo: 31329, com despacho de 2026-09-22, ..." or "Processo n.º 2006 000123, sancionado ...".
    [GeneratedRegex(@"Processo[ \t]*(?:n\.?[ \t]*[º°o]\.?)?[ \t]*:?[ \t]*(?<number>[0-9][^\n,;]{0,40}?)[ \t]*(?:(?<decision>,?\s*(?:com\s+despacho|sancionad))|(?=[,;\n]|$))", RegexOptions.IgnoreCase)]
    private static partial Regex ProcessPattern();

    [GeneratedRegex(@"[ \t]{2,}\d{1,3}$")]
    private static partial Regex PageNumberSuffix();

    // "com despacho de 2026-09-22" or "com despacho concordante da Subdirectora-Geral de 2010-05-05".
    [GeneratedRegex(@"despacho[^;]{0,150}?\bde\s+(\d{4}[-/]\d{2}[-/]\d{2}|\d{2}[-/.]\d{2}[-/.]\d{4})", RegexOptions.IgnoreCase)]
    private static partial Regex DecisionDatePattern();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhiteSpace();
}
