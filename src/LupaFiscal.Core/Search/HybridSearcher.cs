using System.Diagnostics;
using System.Globalization;
using System.Numerics.Tensors;
using LupaFiscal.Core.Embeddings;
using LupaFiscal.Core.Indexing;
using Microsoft.Data.Sqlite;

namespace LupaFiscal.Core.Search;

public enum SearchMode
{
    Hybrid,
    Keyword,
    Vector,
}

/// <summary>
/// Filters applied to both the keyword and the vector list. Null means no filter. Tax and article
/// match the listings of a ruling (a ruling listed by several taxes is found under each, with the
/// article of that listing); an article is only meaningful within one tax, so it requires a tax.
/// </summary>
public sealed record SearchFilters(string? Tax = null, string? Article = null, int? Year = null);

public sealed record SearchOptions
{
    public int Limit { get; init; } = 10;

    /// <summary>Candidates taken from each list before fusion.</summary>
    public int CandidatePool { get; init; } = 100;

    /// <summary>Reciprocal rank fusion constant: score = sum of 1 / (k + rank).</summary>
    public int RrfK { get; init; } = 60;

    public SearchMode Mode { get; init; } = SearchMode.Hybrid;

    /// <summary>One passage per ruling (its best chunk), so the results are distinct rulings.</summary>
    public bool OnePassagePerRuling { get; init; } = true;
}

public sealed record SearchHit(
    long ChunkId,
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

public sealed record SearchResult(string Query, IReadOnlyList<SearchHit> Hits, double ElapsedMs);

/// <summary>
/// Hybrid search: BM25 over the FTS5 table and an exact cosine scan over every chunk vector held
/// in memory, fused with reciprocal rank fusion. The vector matrix is loaded once and read-only, so
/// one searcher can serve concurrent queries; each query opens its own read-only connection.
/// </summary>
public sealed class HybridSearcher
{
    private readonly string _connectionString;
    private readonly IEmbedder _embedder;
    private readonly int _dimensions;
    private readonly float[] _matrix;
    private readonly long[] _chunkIds;
    private readonly int[] _rulingOf;
    private readonly string[] _rulingIds;
    private readonly (string Tax, string Article)[][] _listings;
    private readonly int[] _years;
    private readonly Dictionary<long, int> _rowOfChunk;

    private HybridSearcher(string connectionString, IEmbedder embedder, int dimensions, float[] matrix, long[] chunkIds,
        int[] rulingOf, string[] rulingIds, (string Tax, string Article)[][] listings, int[] years)
    {
        _connectionString = connectionString;
        _embedder = embedder;
        _dimensions = dimensions;
        _matrix = matrix;
        _chunkIds = chunkIds;
        _rulingOf = rulingOf;
        _rulingIds = rulingIds;
        _listings = listings;
        _years = years;
        _rowOfChunk = new Dictionary<long, int>(chunkIds.Length);
        for (var i = 0; i < chunkIds.Length; i++) _rowOfChunk[chunkIds[i]] = i;
    }

    public int ChunkCount => _chunkIds.Length;

    public int RulingCount => _rulingIds.Length;

    /// <summary>Size of the in-memory vector matrix (chunks times dimensions, float32).</summary>
    public long VectorBytes => (long)_matrix.Length * sizeof(float);

    /// <summary>Loads every chunk vector of the index into memory. The index must have been built with the same model.</summary>
    public static HybridSearcher Open(string indexPath, IEmbedder embedder)
    {
        using var database = IndexDatabase.OpenReadOnly(indexPath);
        var model = database.GetMeta("model");
        if (model != embedder.ModelId)
        {
            throw new InvalidDataException($"Index was built with model {model ?? "(none)"}, not {embedder.ModelId}. Run: index");
        }
        var dimensions = embedder.Dimensions;

        var rulingIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        var rulingIds = new List<string>();
        var years = new List<int>();
        using (var command = database.Command("SELECT id, COALESCE(year, 0) FROM rulings ORDER BY id"))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                rulingIndex[reader.GetString(0)] = rulingIds.Count;
                rulingIds.Add(reader.GetString(0));
                years.Add(reader.GetInt32(1));
            }
        }

        var listings = rulingIds.Select(_ => new List<(string, string)>()).ToArray();
        using (var command = database.Command("SELECT ruling_id, tax, article FROM listings ORDER BY id"))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                if (rulingIndex.TryGetValue(reader.GetString(0), out var ruling)) listings[ruling].Add((reader.GetString(1), reader.GetString(2)));
            }
        }

        var chunkIds = new List<long>();
        var rulingOf = new List<int>();
        var matrix = new List<float>();
        using (var command = database.Command("SELECT id, ruling_id, vector FROM chunks WHERE length(vector) = $bytes ORDER BY id",
                   ("$bytes", dimensions * sizeof(float))))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                if (!rulingIndex.TryGetValue(reader.GetString(1), out var ruling)) continue;
                chunkIds.Add(reader.GetInt64(0));
                rulingOf.Add(ruling);
                matrix.AddRange(VectorMath.FromBytes((byte[])reader.GetValue(2)));
            }
        }

        return new HybridSearcher(IndexDatabase.ConnectionString(indexPath, readOnly: true), embedder, dimensions,
            matrix.ToArray(), chunkIds.ToArray(), rulingOf.ToArray(), rulingIds.ToArray(),
            listings.Select(l => l.ToArray()).ToArray(), years.ToArray());
    }

    /// <exception cref="ArgumentException">The filters have an article without a tax.</exception>
    public SearchResult Search(string query, SearchFilters filters, SearchOptions options)
    {
        if (filters.Article is not null && filters.Tax is null)
        {
            throw new ArgumentException("An article filter needs a tax filter: article numbers are only meaningful within one tax code.", nameof(filters));
        }
        var stopwatch = Stopwatch.StartNew();
        var terms = QueryTerms.Extract(query);
        if (string.IsNullOrWhiteSpace(query) || options.Limit <= 0)
        {
            return new SearchResult(query, [], stopwatch.Elapsed.TotalMilliseconds);
        }

        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        var keyword = options.Mode == SearchMode.Vector ? [] : KeywordSearch(connection, terms, filters, options.CandidatePool);
        var vector = options.Mode == SearchMode.Keyword ? [] : VectorSearch(query, filters, options.CandidatePool);
        var ranked = Fuse(keyword, vector, options);
        var hits = Load(connection, ranked, terms);
        return new SearchResult(query, hits, stopwatch.Elapsed.TotalMilliseconds);
    }

    /// <summary>Chunk ids in BM25 order (best first). The expression holds only quoted terms.</summary>
    private static List<long> KeywordSearch(SqliteConnection connection, IReadOnlyList<string> terms, SearchFilters filters, int pool)
    {
        var expression = QueryTerms.ToFtsExpression(terms);
        if (expression is null) return [];
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT c.id
            FROM chunks_fts
            JOIN chunks c ON c.id = chunks_fts.rowid
            JOIN rulings r ON r.id = c.ruling_id
            WHERE chunks_fts MATCH $match
              AND ($tax IS NULL OR EXISTS (
                    SELECT 1 FROM listings l
                    WHERE l.ruling_id = r.id AND l.tax = $tax COLLATE NOCASE
                      AND ($article IS NULL OR l.article = $article COLLATE NOCASE)))
              AND ($year IS NULL OR r.year = $year)
            ORDER BY bm25(chunks_fts), c.id
            LIMIT $pool
            """;
        command.Parameters.AddWithValue("$match", expression);
        command.Parameters.AddWithValue("$tax", (object?)filters.Tax ?? DBNull.Value);
        command.Parameters.AddWithValue("$article", (object?)filters.Article ?? DBNull.Value);
        command.Parameters.AddWithValue("$year", (object?)filters.Year ?? DBNull.Value);
        command.Parameters.AddWithValue("$pool", pool);
        using var reader = command.ExecuteReader();
        var ids = new List<long>();
        while (reader.Read()) ids.Add(reader.GetInt64(0));
        return ids;
    }

    /// <summary>Chunk ids by cosine similarity (best first), over the chunks that pass the filters.</summary>
    private List<long> VectorSearch(string query, SearchFilters filters, int pool)
    {
        if (_chunkIds.Length == 0) return [];
        var allowed = new bool[_rulingIds.Length];
        var any = false;
        for (var r = 0; r < allowed.Length; r++)
        {
            allowed[r] = (filters.Year is null || _years[r] == filters.Year)
                && (filters.Tax is null || Array.Exists(_listings[r], listing =>
                    string.Equals(listing.Tax, filters.Tax, StringComparison.OrdinalIgnoreCase)
                    && (filters.Article is null || string.Equals(listing.Article, filters.Article, StringComparison.OrdinalIgnoreCase))));
            any |= allowed[r];
        }
        if (!any) return [];

        var q = _embedder.EmbedQuery(query);
        if (q.Length != _dimensions) throw new InvalidOperationException("Query vector has the wrong number of dimensions.");

        // Bounded min-heap of the best candidates: O(n log pool).
        var heap = new PriorityQueue<int, (float Score, long NegativeId)>();
        var matrix = _matrix.AsSpan();
        for (var i = 0; i < _chunkIds.Length; i++)
        {
            if (!allowed[_rulingOf[i]]) continue;
            var score = TensorPrimitives.Dot(matrix.Slice(i * _dimensions, _dimensions), q);
            var priority = (score, -_chunkIds[i]);
            if (heap.Count < pool) heap.Enqueue(i, priority);
            else if (heap.TryPeek(out _, out var worst) && priority.CompareTo(worst) > 0) heap.EnqueueDequeue(i, priority);
        }

        var best = new List<(int Row, (float, long) Priority)>(heap.Count);
        while (heap.TryDequeue(out var row, out var priority)) best.Add((row, priority));
        best.Reverse();
        return best.Select(b => _chunkIds[b.Row]).ToList();
    }

    /// <summary>
    /// Reciprocal rank fusion. With one passage per ruling, rulings are ranked by fusing each
    /// list's ruling order (a ruling's rank is that of its first chunk in the list), and the
    /// passage shown is the ruling's chunk with the best chunk-level fused score.
    /// </summary>
    internal List<(long ChunkId, double Score)> Fuse(List<long> keyword, List<long> vector, SearchOptions options)
    {
        var chunkScores = ReciprocalRankFusion.Fuse([keyword, vector], options.RrfK);
        if (!options.OnePassagePerRuling)
        {
            return chunkScores.Take(options.Limit).ToList();
        }

        int RulingOf(long chunkId) => _rowOfChunk.TryGetValue(chunkId, out var row) ? _rulingOf[row] : -1;

        var rulingScores = ReciprocalRankFusion.Fuse(
            [keyword.Select(RulingOf).Where(r => r >= 0).Select(r => (long)r).Distinct().ToList(),
             vector.Select(RulingOf).Where(r => r >= 0).Select(r => (long)r).Distinct().ToList()],
            options.RrfK);
        var bestChunk = new Dictionary<long, long>();
        foreach (var (chunkId, _) in chunkScores)
        {
            bestChunk.TryAdd(RulingOf(chunkId), chunkId);
        }
        return rulingScores.Take(options.Limit).Select(r => (bestChunk[r.Id], r.Score)).ToList();
    }

    private static List<SearchHit> Load(SqliteConnection connection, List<(long ChunkId, double Score)> ranked, IReadOnlyList<string> terms)
    {
        var hits = new List<SearchHit>(ranked.Count);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT c.ruling_id, r.process_number, r.tax, r.article, r.published_on, r.subject, c.section, c.text, r.source_url
            FROM chunks c JOIN rulings r ON r.id = c.ruling_id
            WHERE c.id = $id
            """;
        var id = command.Parameters.Add("$id", SqliteType.Integer);
        foreach (var (chunkId, score) in ranked)
        {
            id.Value = chunkId;
            using var reader = command.ExecuteReader();
            if (!reader.Read()) continue;
            var passage = reader.GetString(7);
            DateOnly? date = reader.IsDBNull(4)
                ? null
                : DateOnly.ParseExact(reader.GetString(4), "yyyy-MM-dd", CultureInfo.InvariantCulture);
            hits.Add(new SearchHit(chunkId, reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                date, reader.GetString(5), reader.GetString(6), passage, QueryTerms.Highlights(passage, terms),
                reader.GetString(8), score));
        }
        return hits;
    }
}
