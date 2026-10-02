using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace LupaFiscal.Core.Indexing;

/// <summary>A section of a ruling body: [Start, End) character offsets into the normalised body.</summary>
public sealed record RulingSection(string Name, int Start, int End);

/// <summary>
/// Cleans the extracted text of a ruling and splits it into the sections found in the corpus.
/// Section names: header (Diploma, Artigo, Assunto, Processo, up to "Conteúdo:"), request
/// (PEDIDO, and the text before the first heading when headings follow), facts (FACTOS,
/// DESCRIÇÃO DOS FACTOS...), legal-framework (INFORMAÇÃO, PARECER, ENQUADRAMENTO, ANÁLISE...),
/// conclusion (CONCLUSÃO, CONCLUSÕES), and content for a ruling without recognisable headings.
/// </summary>
public static partial class RulingSections
{
    public const string Header = "header";
    public const string Request = "request";
    public const string Facts = "facts";
    public const string LegalFramework = "legal-framework";
    public const string Conclusion = "conclusion";
    public const string Content = "content";

    /// <summary>
    /// Removes page furniture repeated on every page (the "INFORMAÇÃO VINCULATIVA" and "FICHA
    /// DOUTRINÁRIA" banners, "1Processo: 25359" footers, "Processo: 2210/2010  1" headers, ministry
    /// letterheads), blank lines and surrounding spaces, so a section continues across page breaks.
    /// Chunk offsets refer to this normalised body, which the index stores.
    /// </summary>
    public static string NormalizeBody(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || IsPageFurniture(line)) continue;
            if (builder.Length > 0) builder.Append('\n');
            builder.Append(line);
        }
        return builder.ToString();
    }

    /// <summary>Splits a normalised body into contiguous, non-empty sections in text order.</summary>
    public static IReadOnlyList<RulingSection> Split(string body)
    {
        var sections = new List<RulingSection>();
        var contentStart = 0;
        var label = ContentLabel().Match(body);
        if (label.Success)
        {
            AddIfNotBlank(sections, body, Header, 0, label.Index);
            contentStart = label.Index + label.Length;
        }

        var headings = new List<(int Start, string Name)>();
        for (var lineStart = contentStart; lineStart < body.Length;)
        {
            var lineEnd = body.IndexOf('\n', lineStart);
            if (lineEnd < 0) lineEnd = body.Length;
            if (HeadingName(body.AsSpan(lineStart, lineEnd - lineStart).ToString()) is { } name)
            {
                headings.Add((lineStart, name));
            }
            lineStart = lineEnd + 1;
        }

        var leadName = headings.Count > 0 ? Request : Content;
        var firstHeading = headings.Count > 0 ? headings[0].Start : body.Length;
        AddIfNotBlank(sections, body, leadName, contentStart, firstHeading);

        var pendingStart = -1;
        for (var i = 0; i < headings.Count; i++)
        {
            var start = pendingStart >= 0 ? pendingStart : headings[i].Start;
            var end = i + 1 < headings.Count ? headings[i + 1].Start : body.Length;
            var headingEnd = body.IndexOf('\n', headings[i].Start);
            var hasText = headingEnd >= 0 && headingEnd < end && !string.IsNullOrWhiteSpace(body[headingEnd..end]);
            if (!hasText && i + 1 < headings.Count)
            {
                // A heading with nothing under it ("PEDIDO:" right before "FACTOS") joins the next section.
                pendingStart = start;
                continue;
            }
            pendingStart = -1;
            AddIfNotBlank(sections, body, headings[i].Name, start, end);
        }
        return sections;
    }

    /// <summary>The section a heading line opens, or null when the line is not a heading.</summary>
    internal static string? HeadingName(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.Length is < 4 or > 80) return null;
        var firstLetter = trimmed.FirstOrDefault(char.IsLetter);
        if (firstLetter == default || !char.IsUpper(firstLetter)) return null;

        var match = HeadingPattern().Match(Fold(trimmed));
        if (!match.Success) return null;
        if (match.Groups["request"].Success) return Request;
        if (match.Groups["facts"].Success) return Facts;
        if (match.Groups["conclusion"].Success) return Conclusion;
        return LegalFramework;
    }

    private static bool IsPageFurniture(string line)
    {
        if (PageProcessLine().IsMatch(line)) return true;
        var folded = Fold(line);
        return folded is "INFORMACAO VINCULATIVA" or "FICHA DOUTRINARIA" or "DIRECCAO GERAL DOS IMPOSTOS"
            || folded.StartsWith("MINISTERIO DAS FINANCAS", StringComparison.Ordinal);
    }

    private static void AddIfNotBlank(List<RulingSection> sections, string body, string name, int start, int end)
    {
        while (start < end && char.IsWhiteSpace(body[start])) start++;
        while (end > start && char.IsWhiteSpace(body[end - 1])) end--;
        if (end > start) sections.Add(new RulingSection(name, start, end));
    }

    /// <summary>Upper case without diacritics, for comparisons only.</summary>
    internal static string Fold(string text)
    {
        var decomposed = text.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) builder.Append(char.ToUpperInvariant(c));
        }
        return builder.ToString().Normalize(NormalizationForm.FormC);
    }

    [GeneratedRegex(@"^Conte[uú]do\s*:\s*", RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ContentLabel();

    // "1Processo: 25359" (footer with the page number glued in front), "Processo: 2210/2010  1"
    // (header followed by the page number). The metadata line "Processo: N, com despacho ..." is kept.
    [GeneratedRegex(@"^\d*\s*Processo:\s*[\w./-]+(?:\s+\d{1,3})?$", RegexOptions.CultureInvariant)]
    private static partial Regex PageProcessLine();

    // Matched against the folded line: optional numbering (I, II., 1 -), then a known heading,
    // then an optional colon. Typos found in the corpus (INFORMACAO, NFORMACAO) are accepted.
    [GeneratedRegex("""
        ^(?:[IVX]{1,4}|\d{1,2})?\s*[.)\-–]?\s*
        (?:
          (?<request>(?:O\s+|DO\s+)?PEDIDO)
        | (?<facts>(?:(?:DESCRICAO|RESUMO)\s+)?(?:D?OS\s+)?FACTOS(?:\s+[A-Z ,-]{0,60})?)
        | (?<conclusion>CONCLUS(?:AO|OES))
        | (?<framework>N?INFORMACAO|PARECER|PONTO\s+PREVIO|FUNDAMENTACAO|APRECIACAO
            |ENQUADRAMENTO(?:\s+(?:JURIDICO-TRIBUTARIO|JURIDICO|LEGAL|FISCAL|TRIBUTARIO))?
            |ANALISE(?:\s+[A-Z ]{0,50})?)
        )
        \s*:?$
        """, RegexOptions.IgnorePatternWhitespace | RegexOptions.CultureInvariant)]
    private static partial Regex HeadingPattern();
}
