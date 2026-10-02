using LupaFiscal.Core.Indexing;
using LupaFiscal.Core.Search;
using LupaFiscal.Tests.Support;
using Microsoft.Data.Sqlite;

namespace LupaFiscal.Tests;

public sealed class HybridSearchTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FakeEmbedder _embedder = new();

    public void Dispose() => _temp.Dispose();

    private string IndexPath => Path.Combine(_temp.Path, "lupa-fiscal.db");

    private IndexBuildSummary BuildIndex(IEnumerable<TestCorpus.Ruling>? rulings = null, ChunkingOptions? options = null)
    {
        var store = TestCorpus.Write(_temp.Path, rulings ?? TestCorpus.All);
        using var database = IndexDatabase.OpenForWrite(IndexPath);
        return new IndexBuilder(database, _embedder, options ?? new ChunkingOptions(), TextWriter.Null).Build(store, CancellationToken.None);
    }

    private HybridSearcher Searcher() => HybridSearcher.Open(IndexPath, _embedder);

    private long Scalar(string sql)
    {
        using var connection = new SqliteConnection(IndexDatabase.ConnectionString(IndexPath, readOnly: true));
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (long)command.ExecuteScalar()!;
    }

    [Fact]
    public void BuildsRulingsChunksFtsRowsAndVectors()
    {
        var summary = BuildIndex();

        Assert.Equal(4, summary.Rulings);
        Assert.Equal(4, summary.Embedded);
        Assert.Equal(summary.ChunksWritten, Scalar("SELECT COUNT(*) FROM chunks"));
        Assert.Equal(summary.ChunksWritten, Scalar($"SELECT COUNT(*) FROM chunks WHERE length(vector) = {_embedder.Dimensions * 4}"));
        Assert.Equal(summary.ChunksWritten, Scalar("SELECT COUNT(*) FROM chunks_fts"));
        Assert.Equal(1, Scalar("SELECT COUNT(*) FROM chunks WHERE ruling_id = 'piv_90004' AND section = 'content'"));
        Assert.Equal(5, Scalar("SELECT COUNT(*) FROM chunks WHERE ruling_id = 'piv_90001'"));
        // Stored offsets point into the stored (normalised) body.
        Assert.Equal(0, Scalar("SELECT COUNT(*) FROM chunks c JOIN rulings r ON r.id = c.ruling_id WHERE substr(r.body, c.start_offset + 1, c.end_offset - c.start_offset) <> c.text"));
    }

    [Fact]
    public void RebuildingIsIdempotentAndAddsOnlyNewRulings()
    {
        BuildIndex([TestCorpus.Education, TestCorpus.CapitalGains, TestCorpus.Rental]);
        var chunks = Scalar("SELECT COUNT(*) FROM chunks");
        var ids = Scalar("SELECT SUM(id) FROM chunks");
        var embedded = _embedder.PassagesEmbedded;

        var again = BuildIndex([TestCorpus.Education, TestCorpus.CapitalGains, TestCorpus.Rental]);

        Assert.Equal((0, 3), (again.Embedded, again.Unchanged));
        Assert.Equal(chunks, Scalar("SELECT COUNT(*) FROM chunks"));
        Assert.Equal(ids, Scalar("SELECT SUM(id) FROM chunks"));
        Assert.Equal(chunks, Scalar("SELECT COUNT(*) FROM chunks_fts"));
        Assert.Equal(embedded, _embedder.PassagesEmbedded);

        var added = BuildIndex(TestCorpus.All);

        Assert.Equal((1, 3), (added.Embedded, added.Unchanged));
        Assert.Equal(chunks + 1, Scalar("SELECT COUNT(*) FROM chunks"));
        Assert.Equal(1, Scalar("SELECT COUNT(*) FROM rulings WHERE id = 'piv_90004'"));
    }

    [Fact]
    public void AChangedTextIsReembeddedAndARemovedRulingLeavesTheIndex()
    {
        BuildIndex();
        var changed = TestCorpus.Pension with { Text = TestCorpus.Pension.Text + "\nAs pensões de alimentos são outra matéria." };

        var summary = BuildIndex([TestCorpus.Education, TestCorpus.CapitalGains, changed]);

        Assert.Equal((1, 2, 1), (summary.Embedded, summary.Unchanged, summary.Removed));
        Assert.Equal(0, Scalar("SELECT COUNT(*) FROM rulings WHERE id = 'piv_90003'"));
        Assert.Equal(0, Scalar("SELECT COUNT(*) FROM chunks WHERE ruling_id = 'piv_90003'"));
        Assert.Equal(Scalar("SELECT COUNT(*) FROM chunks"), Scalar("SELECT COUNT(*) FROM chunks_fts"));
        Assert.Equal(1, Scalar("SELECT COUNT(*) FROM chunks WHERE text LIKE '%pensões de alimentos%'"));
    }

    [Fact]
    public void IndexStatusIsCompleteOnlyWhenEveryExtractedRulingHasVectorChunks()
    {
        BuildIndex([TestCorpus.Education]);
        var store = TestCorpus.Write(_temp.Path, [TestCorpus.Education, TestCorpus.Rental]);
        using (var database = IndexDatabase.OpenForWrite(IndexPath))
        {
            var missing = IndexStatusReport.Compute(database, store, _embedder.ModelId, _embedder.Dimensions, new ChunkingOptions());
            Assert.Equal((2, 1, 1), (missing.Extracted, missing.Indexed, missing.Missing));
            Assert.False(missing.IsComplete);
        }

        BuildIndex([TestCorpus.Education, TestCorpus.Rental]);
        using (var database = IndexDatabase.OpenForWrite(IndexPath))
        {
            var complete = IndexStatusReport.Compute(database, store, _embedder.ModelId, _embedder.Dimensions, new ChunkingOptions());
            Assert.True(complete.IsComplete);

            using var command = database.Command("UPDATE chunks SET vector = NULL WHERE id = (SELECT MIN(id) FROM chunks)");
            command.ExecuteNonQuery();
            var noVector = IndexStatusReport.Compute(database, store, _embedder.ModelId, _embedder.Dimensions, new ChunkingOptions());
            Assert.Equal(noVector.Chunks - 1, noVector.Vectors);
            Assert.False(noVector.IsComplete);
        }

        // A chunk without a vector makes its ruling be embedded again.
        var repaired = BuildIndex([TestCorpus.Education, TestCorpus.Rental]);
        Assert.Equal(1, repaired.Embedded);
        Assert.Equal(Scalar("SELECT COUNT(*) FROM chunks"), Scalar($"SELECT COUNT(*) FROM chunks WHERE length(vector) = {_embedder.Dimensions * 4}"));
    }

    [Fact]
    public void HybridSearchFindsTheRelevantRulingWithHighlightsAndMetadata()
    {
        BuildIndex();

        var result = Searcher().Search("Posso deduzir as despesas de educação dos meus filhos?", new SearchFilters(), new SearchOptions());

        var top = result.Hits[0];
        Assert.Equal("piv_90001", top.RulingId);
        Assert.Equal("78-D", top.Article);
        Assert.Equal(new DateOnly(2024, 3, 12), top.Date);
        Assert.Equal("https://info.portaldasfinancas.gov.pt/pt/informacao_fiscal/informacoes_vinculativas/rendimento/cirs/Documents/PIV_90001.pdf", top.SourceUrl);
        Assert.NotEmpty(top.Highlights);
        Assert.All(top.Highlights, h =>
        {
            Assert.InRange(h.Start, 0, top.Passage.Length - 1);
            Assert.True(h.Start + h.Length <= top.Passage.Length);
            Assert.Contains(QueryTerms.Fold(top.Passage.Substring(h.Start, h.Length)), new[] { "deduzir", "despesas", "educacao", "filhos" });
        });
        // One passage per ruling by default.
        Assert.Equal(result.Hits.Count, result.Hits.Select(h => h.RulingId).Distinct().Count());
        Assert.True(result.ElapsedMs >= 0);
    }

    [Theory]
    [InlineData(SearchMode.Keyword)]
    [InlineData(SearchMode.Vector)]
    [InlineData(SearchMode.Hybrid)]
    public void FiltersApplyToEveryList(SearchMode mode)
    {
        BuildIndex();
        var searcher = Searcher();
        var options = new SearchOptions { Mode = mode, Limit = 50 };
        const string Query = "rendimentos imóvel despesas pensões";

        var all = searcher.Search(Query, new SearchFilters(), options).Hits.Select(h => h.RulingId).ToHashSet();
        var byArticle = searcher.Search(Query, new SearchFilters(Article: "8"), options).Hits;
        var byYear = searcher.Search(Query, new SearchFilters(Year: 2023), options).Hits;
        var byTax = searcher.Search(Query, new SearchFilters(Tax: "cirs"), options).Hits;
        var otherTax = searcher.Search(Query, new SearchFilters(Tax: "IVA"), options).Hits;
        var combined = searcher.Search(Query, new SearchFilters("CIRS", "10", 2023), options).Hits;
        var contradictory = searcher.Search(Query, new SearchFilters("CIRS", "10", 2022), options).Hits;

        Assert.True(all.Count >= 3);
        Assert.Equal(["piv_90003"], byArticle.Select(h => h.RulingId));
        Assert.Equal(["piv_90002"], byYear.Select(h => h.RulingId));
        Assert.Equal(all, byTax.Select(h => h.RulingId).ToHashSet());
        Assert.Empty(otherTax);
        Assert.Equal(["piv_90002"], combined.Select(h => h.RulingId));
        Assert.Empty(contradictory);
    }

    [Fact]
    public void KeywordOnlyAndVectorOnlyModesUseOneList()
    {
        BuildIndex();
        var searcher = Searcher();

        var keyword = searcher.Search("permilagem", new SearchFilters(), new SearchOptions { Mode = SearchMode.Keyword });
        var queriesBefore = _embedder.QueriesEmbedded;
        searcher.Search("permilagem", new SearchFilters(), new SearchOptions { Mode = SearchMode.Keyword });

        Assert.Equal(["piv_90003"], keyword.Hits.Select(h => h.RulingId));
        Assert.Equal(queriesBefore, _embedder.QueriesEmbedded); // keyword mode never embeds the query
        var vector = searcher.Search("permilagem", new SearchFilters(), new SearchOptions { Mode = SearchMode.Vector });
        Assert.Equal(4, vector.Hits.Count); // every ruling has a vector score
    }

    [Theory]
    [InlineData("\"")]
    [InlineData("\"despesas de educação")]
    [InlineData("despesas\" OR \"x")]
    [InlineData("NEAR(despesas educação, 2)")]
    [InlineData("despesas NEAR educação")]
    [InlineData("despesas AND educação")]
    [InlineData("despesas OR")]
    [InlineData("NOT despesas")]
    [InlineData("AND OR NOT")]
    [InlineData("*")]
    [InlineData("despes*")]
    [InlineData("(despesas")]
    [InlineData("despesas)")]
    [InlineData("()")]
    [InlineData("text:despesas")]
    [InlineData("chunks_fts:educação")]
    [InlineData("{text}: educação")]
    [InlineData("-")]
    [InlineData("- -")]
    [InlineData("-despesas")]
    [InlineData("despesas - educação")]
    [InlineData("^despesas")]
    [InlineData("+")]
    [InlineData("'; DROP TABLE chunks; --")]
    [InlineData("\0")]
    [InlineData("   ")]
    [InlineData("…")]
    public void UserTextNeverReachesFtsAsSyntax(string query)
    {
        BuildIndex();
        var searcher = Searcher();

        foreach (var mode in new[] { SearchMode.Keyword, SearchMode.Hybrid })
        {
            var result = searcher.Search(query, new SearchFilters(), new SearchOptions { Mode = mode });
            Assert.NotNull(result.Hits);
        }

        var expression = QueryTerms.ToFtsExpression(QueryTerms.Extract(query));
        if (expression is not null)
        {
            // The generated expression is valid FTS5 on its own.
            using var connection = new SqliteConnection(IndexDatabase.ConnectionString(IndexPath, readOnly: true));
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM chunks_fts WHERE chunks_fts MATCH $match";
            command.Parameters.AddWithValue("$match", expression);
            Assert.True((long)command.ExecuteScalar()! >= 0);
        }
        Assert.Equal(4, Scalar("SELECT COUNT(*) FROM rulings"));
    }

    [Fact]
    public void OperatorWordsAreSearchedAsPlainWords()
    {
        var terms = QueryTerms.Extract("despesas AND \"educação\" NEAR filhos*");

        Assert.Equal(["despesas", "and", "educação", "near", "filhos"], terms);
        Assert.Equal("\"despesas\" OR \"and\" OR \"educação\" OR \"near\" OR \"filhos\"", QueryTerms.ToFtsExpression(terms));
        Assert.Null(QueryTerms.ToFtsExpression(QueryTerms.Extract("\"()*-:^")));
        Assert.Equal(["de", "a"], QueryTerms.Extract("de a")); // only stop words: keep them
    }

    [Fact]
    public void ReciprocalRankFusionUsesKSixtyAndBreaksTiesDeterministically()
    {
        var fused = ReciprocalRankFusion.Fuse([[10L, 20L, 30L], [30L, 10L, 40L]], 60);

        Assert.Equal([10L, 30L, 20L, 40L], fused.Select(f => f.Id));
        Assert.Equal(1.0 / 61 + 1.0 / 62, fused[0].Score, 12);
        Assert.Equal(1.0 / 63 + 1.0 / 61, fused[1].Score, 12);
        Assert.Equal(1.0 / 62, fused[2].Score, 12);

        var tie = ReciprocalRankFusion.Fuse([[2L, 1L], [1L, 2L]], 60);
        Assert.Equal([1L, 2L], tie.Select(f => f.Id)); // equal scores and best ranks: lower id first
        var duplicates = ReciprocalRankFusion.Fuse([[5L, 5L, 6L]], 60);
        Assert.Equal(1.0 / 62, duplicates.Single(f => f.Id == 6).Score, 12);
    }

    [Fact]
    public void SearcherRefusesAnIndexBuiltWithAnotherModel()
    {
        BuildIndex();

        var error = Assert.Throws<InvalidDataException>(() => HybridSearcher.Open(IndexPath, new OtherModel(new FakeEmbedder())));

        Assert.Contains("Run: index", error.Message, StringComparison.Ordinal);
    }

    private sealed class OtherModel(FakeEmbedder inner) : Core.Embeddings.IEmbedder
    {
        public string ModelId => "other/model@2";
        public int Dimensions => inner.Dimensions;
        public int MaxTokens => inner.MaxTokens;
        public int CountTokens(string text) => inner.CountTokens(text);
        public int CountPassageTokens(string text) => inner.CountPassageTokens(text);
        public float[] EmbedQuery(string query) => inner.EmbedQuery(query);
        public IReadOnlyList<float[]> EmbedPassages(IReadOnlyList<string> passages, CancellationToken cancellationToken) =>
            inner.EmbedPassages(passages, cancellationToken);
        public void Dispose() => inner.Dispose();
    }
}
