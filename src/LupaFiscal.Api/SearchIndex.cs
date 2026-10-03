using LupaFiscal.Core.Crawling;
using LupaFiscal.Core.Embeddings;
using LupaFiscal.Core.Indexing;
using LupaFiscal.Core.Search;
using Microsoft.Data.Sqlite;

namespace LupaFiscal.Api;

/// <summary>
/// The index as served by the API: the hybrid searcher (vectors in memory) and the facet counts,
/// both read once at startup from the same index file. Read-only and safe for concurrent requests.
/// </summary>
public sealed class SearchIndex
{
    private static readonly UrlPolicy SourcePolicy = new(TaxSource.All.Select(source => source.Origin.IdnHost));

    private readonly HybridSearcher _searcher;

    private SearchIndex(HybridSearcher searcher, FacetsResponse facets)
    {
        _searcher = searcher;
        Facets = facets;
    }

    public FacetsResponse Facets { get; }

    public int Rulings => _searcher.RulingCount;

    public int Chunks => _searcher.ChunkCount;

    public static SearchIndex Open(string indexPath, IEmbedder embedder)
    {
        var searcher = HybridSearcher.Open(indexPath, embedder);
        return new SearchIndex(searcher, LoadFacets(indexPath));
    }

    public SearchResponse Search(SearchQuery query)
    {
        var result = _searcher.Search(query.Text, new SearchFilters(query.Tax, query.Article, query.Year),
            new SearchOptions { Limit = query.Limit });
        var items = new List<SearchResultItem>(result.Hits.Count);
        foreach (var hit in result.Hits)
        {
            // Only https links on the official host leave the API; anything else is not shown at all.
            if (OfficialSourceUrl(hit.SourceUrl) is not { } sourceUrl) continue;
            items.Add(new SearchResultItem(hit.RulingId, hit.ProcessNumber, hit.Tax, hit.Article, hit.Date, hit.Subject,
                hit.Section, hit.Passage, ValidHighlights(hit), sourceUrl, Math.Round(hit.Score, 6)));
        }
        return new SearchResponse(result.Query, Math.Round(result.ElapsedMs, 1), items);
    }

    /// <summary>The URL in canonical form when it is https on an allowlisted host, otherwise null.</summary>
    internal static string? OfficialSourceUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && SourcePolicy.IsAllowed(uri) ? uri.AbsoluteUri : null;

    private static IReadOnlyList<Highlight> ValidHighlights(SearchHit hit) =>
        hit.Highlights.Where(h => h.Start >= 0 && h.Length > 0 && h.Start + h.Length <= hit.Passage.Length).ToList();

    /// <summary>
    /// Counts of rulings that have at least one chunk. Taxes and articles are counted per listing, so
    /// they match the filters: a ruling listed by two taxes counts once in each, under the article it
    /// has there. Years count each ruling once, by its publication year.
    /// </summary>
    private static FacetsResponse LoadFacets(string indexPath)
    {
        using var connection = new SqliteConnection(IndexDatabase.ConnectionString(indexPath, readOnly: true));
        connection.Open();

        List<(string Tax, string Value, int Count)> Counts(string sql)
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            using var reader = command.ExecuteReader();
            var counts = new List<(string, string, int)>();
            while (reader.Read()) counts.Add((reader.GetString(0), reader.GetString(1), reader.GetInt32(2)));
            return counts;
        }

        const string HasChunks = "EXISTS (SELECT 1 FROM chunks c WHERE c.ruling_id = l.ruling_id)";
        var taxes = Counts($"""
                SELECT l.tax, l.tax, COUNT(DISTINCT l.ruling_id) FROM listings l
                WHERE TRIM(l.tax) <> '' AND {HasChunks}
                GROUP BY l.tax
                """)
            .OrderBy(c => c.Value, StringComparer.Ordinal)
            .Select(c => new FacetValue(c.Value, c.Count)).ToList();
        var articles = Counts($"""
                SELECT l.tax, l.article, COUNT(DISTINCT l.ruling_id) FROM listings l
                WHERE TRIM(l.tax) <> '' AND TRIM(l.article) <> '' AND {HasChunks}
                GROUP BY l.tax, l.article
                """)
            .OrderBy(c => c.Tax, StringComparer.Ordinal)
            .ThenBy(c => ArticleNumber(c.Value)).ThenBy(c => c.Value, StringComparer.Ordinal)
            .Select(c => new ArticleFacetValue(c.Tax, c.Value, c.Count)).ToList();
        var years = Counts("""
                SELECT '', CAST(r.year AS TEXT), COUNT(*) FROM rulings r
                WHERE r.year IS NOT NULL AND EXISTS (SELECT 1 FROM chunks c WHERE c.ruling_id = r.id)
                GROUP BY r.year
                """)
            .Select(c => (Year: int.Parse(c.Value, System.Globalization.CultureInfo.InvariantCulture), c.Count))
            .Where(c => c.Year > 0)
            .OrderByDescending(c => c.Year)
            .Select(c => new YearFacetValue(c.Year, c.Count)).ToList();
        return new FacetsResponse(taxes, articles, years);
    }

    /// <summary>Leading number of an article ("78-D" is 78), so articles sort as 2, 10, 78-D rather than 10, 2, 78-D.</summary>
    private static int ArticleNumber(string article)
    {
        var digits = article.TakeWhile(char.IsAsciiDigit).Count();
        return digits is > 0 and < 9 ? int.Parse(article.AsSpan(0, digits), System.Globalization.CultureInfo.InvariantCulture) : int.MaxValue;
    }
}
