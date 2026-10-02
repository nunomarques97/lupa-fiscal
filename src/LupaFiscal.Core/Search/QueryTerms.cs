using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace LupaFiscal.Core.Search;

/// <summary>
/// Turns user text into search terms. The FTS5 expression is built only from these terms, each
/// one double-quoted, so the user's quotes, operators (AND, OR, NOT, NEAR), wildcards, parentheses,
/// column filters or hyphens are never parsed as FTS5 syntax.
/// </summary>
public static partial class QueryTerms
{
    public const int MaxTerms = 32;

    // Common Portuguese function words; they add noise to an OR query over legal text.
    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "a", "ao", "aos", "as", "com", "como", "da", "das", "de", "do", "dos", "e", "ela", "elas", "ele",
        "eles", "em", "entre", "essa", "esse", "esta", "este", "eu", "foi", "ha", "isso", "isto", "ja", "lhe",
        "mais", "mas", "me", "meu", "meus", "minha", "minhas", "na", "nas", "nao", "nem", "no", "nos", "num",
        "numa", "o", "os", "ou", "para", "pela", "pelas", "pelo", "pelos", "pode", "por", "posso", "qual",
        "quais", "quando", "que", "se", "ser", "seu", "seus", "sua", "suas", "sao", "sem", "sobre", "tem",
        "ter", "um", "uma", "uns", "umas", "eh", "devo", "deve", "sim", "tambem", "so",
    };

    /// <summary>
    /// Distinct words of the query (letters, digits and combining marks), lower case, in order,
    /// without stop words unless nothing else is left. At most <see cref="MaxTerms"/>.
    /// </summary>
    public static IReadOnlyList<string> Extract(string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];
        var words = Word().Matches(query.Normalize(NormalizationForm.FormC))
            .Select(m => m.Value.ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var content = words.Where(w => !StopWords.Contains(Fold(w))).ToList();
        return (content.Count > 0 ? content : words).Take(MaxTerms).ToList();
    }

    /// <summary>An FTS5 MATCH expression: every term quoted, joined with OR. Null when there is no term.</summary>
    public static string? ToFtsExpression(IReadOnlyList<string> terms) =>
        terms.Count == 0 ? null : string.Join(" OR ", terms.Select(t => "\"" + t.Replace("\"", "\"\"", StringComparison.Ordinal) + "\""));

    /// <summary>Lower case without diacritics, matching the FTS5 unicode61 remove_diacritics folding.</summary>
    public static string Fold(string text)
    {
        var decomposed = text.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) builder.Append(char.ToLowerInvariant(c));
        }
        return builder.ToString();
    }

    /// <summary>
    /// Offsets of the words of <paramref name="passage"/> that match a term (case and diacritics
    /// ignored), in passage order. Offsets index the passage string; no markup is produced.
    /// </summary>
    public static IReadOnlyList<Highlight> Highlights(string passage, IReadOnlyList<string> terms)
    {
        if (terms.Count == 0) return [];
        var folded = terms.Select(Fold).ToHashSet(StringComparer.Ordinal);
        var result = new List<Highlight>();
        foreach (Match word in Word().Matches(passage))
        {
            if (folded.Contains(Fold(word.Value))) result.Add(new Highlight(word.Index, word.Length));
        }
        return result;
    }

    [GeneratedRegex(@"[\p{L}\p{N}\p{M}]+")]
    private static partial Regex Word();
}

public readonly record struct Highlight(int Start, int Length);
