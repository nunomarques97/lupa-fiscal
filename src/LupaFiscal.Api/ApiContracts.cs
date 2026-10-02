using LupaFiscal.Core.Search;

namespace LupaFiscal.Api;

// JSON shapes of the API (camelCase on the wire). Text is plain: highlights are offsets into the
// passage (UTF-16 code units, as in JavaScript strings), never markup.

public sealed record SearchResponse(string Query, double TookMs, IReadOnlyList<SearchResultItem> Results);

public sealed record SearchResultItem(
    string RulingId,
    string ProcessNumber,
    string Tax,
    string Article,
    DateOnly? Date,
    string Subject,
    string Section,
    string Passage,
    IReadOnlyList<Highlight> Highlights,
    string SourceUrl,
    double Score);

public sealed record FacetValue(string Value, int Count);

public sealed record YearFacetValue(int Value, int Count);

public sealed record FacetsResponse(
    IReadOnlyList<FacetValue> Taxes,
    IReadOnlyList<FacetValue> Articles,
    IReadOnlyList<YearFacetValue> Years);

public sealed record HealthResponse(string Status, int Rulings, int Chunks);
