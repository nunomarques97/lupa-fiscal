using System.Globalization;

namespace LupaFiscal.Api;

/// <summary>A validated GET /api/search request.</summary>
public sealed record SearchQuery(string Text, string? Tax, string? Article, int? Year, int Limit)
{
    public const int MaxQueryLength = 500;
    public const int DefaultLimit = 10;
    public const int MinLimit = 1;
    public const int MaxLimit = 50;
    public const int MinYear = 1900;
    public const int MaxYear = 2100;

    /// <summary>
    /// Validates the query string. Every parameter may appear at most once. q is required and not
    /// blank, at most <see cref="MaxQueryLength"/> characters. tax and article are free text: blank
    /// means no filter and an unknown value simply matches nothing; an article is only meaningful
    /// within one tax code, so an article without a tax is rejected. year and limit must be plain
    /// decimal digits within range (out-of-range values are rejected, never clamped); an empty value
    /// means the parameter is absent, as an empty form field would send it.
    /// </summary>
    public static SearchQuery? Parse(IQueryCollection query, out Dictionary<string, string[]> errors)
    {
        errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

        var q = Single(query, "q", errors);
        if (q is not null)
        {
            if (string.IsNullOrWhiteSpace(q)) errors["q"] = ["The question must not be blank."];
            else if (q.Length > MaxQueryLength) errors["q"] = [$"The question must be at most {MaxQueryLength} characters."];
        }
        else if (!errors.ContainsKey("q"))
        {
            errors["q"] = ["The question (q) is required."];
        }

        var tax = Blank(Single(query, "tax", errors));
        var article = Blank(Single(query, "article", errors));
        if (article is not null && tax is null && !errors.ContainsKey("tax"))
        {
            errors["article"] = ["The article filter needs a tax (tax): article numbers are only meaningful within one tax code."];
        }
        var year = Number(query, "year", MinYear, MaxYear, errors);
        var limit = Number(query, "limit", MinLimit, MaxLimit, errors) ?? DefaultLimit;

        return errors.Count == 0 ? new SearchQuery(q!.Trim(), tax, article, year, limit) : null;
    }

    private static string? Single(IQueryCollection query, string name, Dictionary<string, string[]> errors)
    {
        if (!query.TryGetValue(name, out var values) || values.Count == 0) return null;
        if (values.Count > 1)
        {
            errors[name] = [$"The parameter {name} must be given at most once."];
            return null;
        }
        return values[0] ?? string.Empty;
    }

    private static int? Number(IQueryCollection query, string name, int min, int max, Dictionary<string, string[]> errors)
    {
        var text = Single(query, name, errors);
        if (string.IsNullOrEmpty(text)) return null;
        // Digits only: no sign, spaces, separators or exponent. Too many digits fail to parse and count as out of range.
        if (text.All(char.IsAsciiDigit) && int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            && value >= min && value <= max)
        {
            return value;
        }
        errors[name] = [$"The parameter {name} must be a whole number from {min} to {max}."];
        return null;
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
