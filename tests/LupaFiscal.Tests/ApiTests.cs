using System.Net;
using System.Text.Json;
using LupaFiscal.Core.Embeddings;
using LupaFiscal.Core.Indexing;
using LupaFiscal.Core.Search;
using LupaFiscal.Tests.Support;
using Microsoft.Data.Sqlite;

namespace LupaFiscal.Tests;

public sealed class ApiHostFixture : IDisposable
{
    internal ApiTestHost Host { get; } = new();

    public void Dispose() => Host.Dispose();
}

public sealed class ApiTests(ApiHostFixture fixture) : IClassFixture<ApiHostFixture>
{
    private const string Gains = "mais-valias alienação imóvel";
    private const string OfficialPrefix = "https://info.portaldasfinancas.gov.pt/";

    private HttpClient Client => fixture.Host.Client;

    private async Task<(HttpResponseMessage Response, JsonElement Body, string Raw)> Get(string url)
    {
        var response = await Client.GetAsync(url);
        var raw = await response.Content.ReadAsStringAsync();
        var body = raw.Length > 0 && response.Content.Headers.ContentType?.MediaType?.Contains("json") == true
            ? JsonDocument.Parse(raw).RootElement.Clone()
            : default;
        return (response, body, raw);
    }

    private async Task<List<JsonElement>> Results(string url)
    {
        var (response, body, _) = await Get(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return body.GetProperty("results").EnumerateArray().ToList();
    }

    private static string Q(string text) => Uri.EscapeDataString(text);

    private static HashSet<string> Ids(IEnumerable<JsonElement> results) =>
        results.Select(r => r.GetProperty("rulingId").GetString()!).ToHashSet();

    [Fact]
    public async Task SearchReturnsTheContractShape()
    {
        var (response, body, raw) = await Get($"/api/search?q={Q("Posso deduzir as despesas de educação dos meus filhos?")}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(["query", "tookMs", "results"], body.EnumerateObject().Select(p => p.Name));
        Assert.Equal("Posso deduzir as despesas de educação dos meus filhos?", body.GetProperty("query").GetString());
        Assert.True(body.GetProperty("tookMs").GetDouble() >= 0);
        var results = body.GetProperty("results").EnumerateArray().ToList();
        Assert.Equal(ApiTestHost.Corpus.Count, results.Count);
        Assert.Equal(results.Count, Ids(results).Count); // one passage per ruling

        var first = results[0];
        Assert.Equal(
            ["rulingId", "processNumber", "tax", "article", "date", "subject", "section", "passage", "highlights", "sourceUrl", "score"],
            first.EnumerateObject().Select(p => p.Name));
        Assert.Equal("piv_90001", first.GetProperty("rulingId").GetString());
        Assert.Equal("90001", first.GetProperty("processNumber").GetString());
        Assert.Equal("CIRS", first.GetProperty("tax").GetString());
        Assert.Equal("78-D", first.GetProperty("article").GetString());
        Assert.Equal("2024-03-12", first.GetProperty("date").GetString());
        Assert.Equal(TestCorpus.Education.Subject, first.GetProperty("subject").GetString());
        Assert.False(string.IsNullOrEmpty(first.GetProperty("section").GetString()));
        Assert.Contains("educação", first.GetProperty("passage").GetString()!, StringComparison.OrdinalIgnoreCase);
        Assert.NotEmpty(first.GetProperty("highlights").EnumerateArray());
        Assert.Equal(OfficialPrefix + "pt/informacao_fiscal/informacoes_vinculativas/rendimento/cirs/Documents/PIV_90001.pdf",
            first.GetProperty("sourceUrl").GetString());
        var scores = results.Select(r => r.GetProperty("score").GetDouble()).ToList();
        Assert.Equal(scores.OrderByDescending(s => s), scores);
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.DoesNotContain('<', raw);
    }

    [Fact]
    public async Task HighlightsAreOffsetsInsideThePassageAndNoHtmlIsReturned()
    {
        var (_, body, raw) = await Get($"/api/search?q={Q("despesas de educação propinas mais-valias script alert")}&limit=50");
        var terms = QueryTerms.Extract("despesas de educação propinas mais-valias script alert").Select(QueryTerms.Fold).ToHashSet();
        var results = body.GetProperty("results").EnumerateArray().ToList();
        var highlighted = 0;

        foreach (var result in results)
        {
            var passage = result.GetProperty("passage").GetString()!;
            foreach (var highlight in result.GetProperty("highlights").EnumerateArray())
            {
                Assert.Equal(["start", "length"], highlight.EnumerateObject().Select(p => p.Name));
                var start = highlight.GetProperty("start").GetInt32();
                var length = highlight.GetProperty("length").GetInt32();
                Assert.InRange(start, 0, passage.Length - 1);
                Assert.InRange(length, 1, passage.Length - start);
                Assert.Contains(QueryTerms.Fold(passage.Substring(start, length)), terms);
                highlighted++;
            }
            Assert.StartsWith(OfficialPrefix, result.GetProperty("sourceUrl").GetString());
        }

        Assert.True(highlighted > 5);
        // Markup in a ruling's text is returned as text, escaped in the JSON; the API never emits HTML.
        var markup = results.Single(r => r.GetProperty("rulingId").GetString() == "piv_90006").GetProperty("passage").GetString()!;
        Assert.Contains("<script>alert(1)</script>", markup);
        Assert.DoesNotContain('<', raw);
        Assert.DoesNotContain("<mark", raw, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task EachFilterRestrictsTheResults()
    {
        var all = Ids(await Results($"/api/search?q={Q(Gains)}&limit=50"));
        Assert.Equal(ApiTestHost.Corpus.Select(r => r.Id).ToHashSet(), all);

        Assert.Equal(["piv_90006"], Ids(await Results($"/api/search?q={Q(Gains)}&limit=50&tax=CIRC")));
        Assert.Equal(all.Except(["piv_90006"]).ToHashSet(), Ids(await Results($"/api/search?q={Q(Gains)}&limit=50&tax=cirs")));
        Assert.Equal(["piv_90002", "piv_90005", "piv_90006"], Ids(await Results($"/api/search?q={Q(Gains)}&limit=50&article=10")));
        Assert.Equal(["piv_90001"], Ids(await Results($"/api/search?q={Q(Gains)}&limit=50&article=78-d")));
        Assert.Equal(["piv_90002", "piv_90005"], Ids(await Results($"/api/search?q={Q(Gains)}&limit=50&year=2023")));
    }

    [Fact]
    public async Task FiltersCombine()
    {
        Assert.Equal(["piv_90002", "piv_90005"], Ids(await Results($"/api/search?q={Q(Gains)}&limit=50&tax=CIRS&article=10&year=2023")));
        Assert.Equal(["piv_90006"], Ids(await Results($"/api/search?q={Q(Gains)}&limit=50&article=10&year=2024")));
        Assert.Empty(await Results($"/api/search?q={Q(Gains)}&limit=50&tax=CIRC&year=2023"));
        // Blank filters and an empty year mean no filter.
        Assert.Equal(ApiTestHost.Corpus.Count, (await Results($"/api/search?q={Q(Gains)}&limit=50&tax=&article=%20&year=")).Count);
    }

    [Theory]
    [InlineData("tax=IVA")]
    [InlineData("article=999-Z")]
    [InlineData("tax=CIRS&article=12345678901234567890")]
    [InlineData("tax=%27%20OR%201%3D1%20--")]
    public async Task AnUnknownTaxOrArticleReturnsNoResults(string filter)
    {
        var (response, body, _) = await Get($"/api/search?q={Q(Gains)}&{filter}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(body.GetProperty("results").EnumerateArray());
    }

    [Fact]
    public async Task LimitBoundsTheResultsAndDefaultsToTen()
    {
        Assert.Equal(2, (await Results($"/api/search?q={Q(Gains)}&limit=2")).Count);
        Assert.Single(await Results($"/api/search?q={Q(Gains)}&limit=1"));
        Assert.Equal(ApiTestHost.Corpus.Count, (await Results($"/api/search?q={Q(Gains)}&limit=50")).Count);
        Assert.Equal(ApiTestHost.Corpus.Count, (await Results($"/api/search?q={Q(Gains)}")).Count); // 6 rulings, default 10
        Assert.Equal(200, (int)(await Get($"/api/search?q={new string('a', 500)}")).Response.StatusCode);
    }

    [Theory]
    [InlineData("", "q")]
    [InlineData("q=", "q")]
    [InlineData("q=%20%20%09", "q")]
    [InlineData("tax=CIRS", "q")]
    [InlineData("q=a&q=b", "q")]
    [InlineData("q=LONG", "q")]
    [InlineData("q=x&year=abc", "year")]
    [InlineData("q=x&year=2024.0", "year")]
    [InlineData("q=x&year=-2024", "year")]
    [InlineData("q=x&year=%202024", "year")]
    [InlineData("q=x&year=1899", "year")]
    [InlineData("q=x&year=2101", "year")]
    [InlineData("q=x&year=99999999999999999999", "year")]
    [InlineData("q=x&year=2023&year=2024", "year")]
    [InlineData("q=x&limit=abc", "limit")]
    [InlineData("q=x&limit=0", "limit")]
    [InlineData("q=x&limit=51", "limit")]
    [InlineData("q=x&limit=1000", "limit")]
    [InlineData("q=x&limit=-1", "limit")]
    [InlineData("q=x&limit=1e1", "limit")]
    [InlineData("q=x&limit=99999999999", "limit")]
    [InlineData("q=x&tax=CIRS&tax=CIRC", "tax")]
    public async Task InvalidRequestsReturn400ProblemDetails(string queryString, string field)
    {
        var url = "/api/search?" + queryString.Replace("q=LONG", "q=" + new string('a', LupaFiscal.Api.SearchQuery.MaxQueryLength + 1), StringComparison.Ordinal);

        var (response, body, _) = await Get(url);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(400, body.GetProperty("status").GetInt32());
        Assert.True(body.GetProperty("errors").TryGetProperty(field, out var messages));
        Assert.NotEmpty(messages.EnumerateArray());
        Assert.False(body.TryGetProperty("results", out _));
    }

    [Theory]
    [InlineData("\"")]
    [InlineData("\"despesas de educação")]
    [InlineData("despesas\" OR \"x")]
    [InlineData("NEAR(despesas educação, 2)")]
    [InlineData("despesas AND NOT educação")]
    [InlineData("despes* OR")]
    [InlineData("(despesas")]
    [InlineData("text:despesas")]
    [InlineData("{text}: despesas ^educação")]
    [InlineData("-despesas +educação")]
    [InlineData("'; DROP TABLE rulings; --")]
    [InlineData("???")]
    public async Task FtsSyntaxInTheQueryIsSearchedAsText(string query)
    {
        var (response, body, _) = await Get($"/api/search?q={Q(query)}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(query.Trim(), body.GetProperty("query").GetString());
        Assert.NotEmpty(body.GetProperty("results").EnumerateArray());
    }

    [Fact]
    public async Task FacetsCountRulingsPerTaxArticleAndYear()
    {
        var (response, body, _) = await Get("/api/facets");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(["taxes", "articles", "years"], body.EnumerateObject().Select(p => p.Name));
        static List<(string, int)> Pairs(JsonElement list) => list.EnumerateArray()
            .Select(v => (v.GetProperty("value").ToString(), v.GetProperty("count").GetInt32())).ToList();

        Assert.Equal([("CIRC", 1), ("CIRS", 5)], Pairs(body.GetProperty("taxes")));
        Assert.Equal([("8", 1), ("10", 3), ("11", 1), ("78-D", 1)], Pairs(body.GetProperty("articles")));
        Assert.Equal([("2024", 2), ("2023", 2), ("2022", 1), ("2021", 1)], Pairs(body.GetProperty("years")));
        Assert.Equal(JsonValueKind.Number, body.GetProperty("years")[0].GetProperty("value").ValueKind);
    }

    [Fact]
    public async Task HealthReportsTheIndexSize()
    {
        var (response, body, _) = await Get("/api/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("ok", body.GetProperty("status").GetString());
        Assert.Equal(ApiTestHost.Corpus.Count, body.GetProperty("rulings").GetInt32());
        using var connection = new SqliteConnection(IndexDatabase.ConnectionString(fixture.Host.IndexPath, readOnly: true));
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM chunks";
        Assert.Equal((long)command.ExecuteScalar()!, body.GetProperty("chunks").GetInt64());
    }

    [Fact]
    public async Task UnknownApiPathsAndMethodsAreNotTheSpa()
    {
        var (missing, body, _) = await Get("/api/nothing/here");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal("application/problem+json", missing.Content.Headers.ContentType?.MediaType);
        Assert.Equal(404, body.GetProperty("status").GetInt32());

        // Other methods are not part of the API: a JSON 404 as well.
        var post = await Client.PostAsync("/api/search?q=x", null);
        Assert.Equal(HttpStatusCode.NotFound, post.StatusCode);
        Assert.Equal("application/problem+json", post.Content.Headers.ContentType?.MediaType);

        // No built app configured: the root is not served.
        Assert.Equal(HttpStatusCode.NotFound, (await Client.GetAsync("/")).StatusCode);
    }

    [Fact]
    public async Task OnlyLocalHostNamesAreAccepted()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/health");
        request.Headers.Host = "attacker.example";

        var response = await Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ConcurrentSearchesAllSucceed()
    {
        var responses = await Task.WhenAll(Enumerable.Range(0, 16).Select(i => Client.GetAsync($"/api/search?q={Q(Gains)}&limit={1 + i % 5}")));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
    }
}

/// <summary>API behaviour that needs its own index or services.</summary>
public sealed class ApiHostTests
{
    [Fact]
    public async Task ResultsWithANonOfficialSourceUrlAreNeverReturned()
    {
        using var host = new ApiTestHost(prepareIndex: path =>
        {
            using var connection = new SqliteConnection(IndexDatabase.ConnectionString(path, readOnly: false));
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE rulings SET source_url = 'http://info.portaldasfinancas.gov.pt/x.pdf' WHERE id = 'piv_90002';
                UPDATE rulings SET source_url = 'https://attacker.example/x.pdf' WHERE id = 'piv_90003';
                UPDATE rulings SET source_url = 'javascript:alert(1)' WHERE id = 'piv_90004';
                UPDATE rulings SET source_url = 'https://info.portaldasfinancas.gov.pt.attacker.example/x.pdf' WHERE id = 'piv_90005';
                """;
            command.ExecuteNonQuery();
        });

        var body = JsonDocument.Parse(await host.Client.GetStringAsync("/api/search?q=rendimentos&limit=50")).RootElement;
        var results = body.GetProperty("results").EnumerateArray().ToList();

        Assert.Equal(["piv_90001", "piv_90006"], results.Select(r => r.GetProperty("rulingId").GetString()).Order());
        Assert.All(results, r => Assert.StartsWith("https://info.portaldasfinancas.gov.pt/", r.GetProperty("sourceUrl").GetString()));
    }

    [Fact]
    public async Task UnhandledErrorsReturn500WithoutDetails()
    {
        using var host = new ApiTestHost(embedder: new FailingEmbedder());

        var response = await host.Client.GetAsync("/api/search?q=despesas");
        var raw = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(500, JsonDocument.Parse(raw).RootElement.GetProperty("status").GetInt32());
        Assert.DoesNotContain(FailingEmbedder.Secret, raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Exception", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("LupaFiscal", raw, StringComparison.Ordinal);
        Assert.DoesNotContain(":\\", raw, StringComparison.Ordinal);
        Assert.DoesNotContain(".cs", raw, StringComparison.Ordinal);
        // The API keeps serving after an error.
        Assert.Equal(HttpStatusCode.OK, (await host.Client.GetAsync("/api/health")).StatusCode);
    }

    [Fact]
    public async Task ServesTheBuiltAppWithAnSpaFallback()
    {
        using var temp = new TempDirectory();
        var webRoot = Directory.CreateDirectory(Path.Combine(temp.Path, "dist")).FullName;
        const string Index = "<!doctype html><title>app</title><app-root></app-root>";
        File.WriteAllText(Path.Combine(webRoot, "index.html"), Index);
        File.WriteAllText(Path.Combine(webRoot, "main.js"), "console.log(1);");
        File.WriteAllText(Path.Combine(temp.Path, "outside.txt"), "outside");
        using var host = new ApiTestHost(webRoot: webRoot);

        Assert.Equal(Index, await host.Client.GetStringAsync("/"));
        Assert.Equal(Index, await host.Client.GetStringAsync("/pesquisa?q=despesas&ano=2024"));
        var script = await host.Client.GetAsync("/main.js");
        Assert.Equal(HttpStatusCode.OK, script.StatusCode);
        Assert.Equal("text/javascript", script.Content.Headers.ContentType?.MediaType);
        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.GetAsync("/missing.js")).StatusCode);
        Assert.NotEqual(HttpStatusCode.OK, (await host.Client.GetAsync("/%2E%2E/outside.txt")).StatusCode);

        var api = await host.Client.GetAsync("/api/unknown");
        Assert.Equal(HttpStatusCode.NotFound, api.StatusCode);
        Assert.Equal("application/problem+json", api.Content.Headers.ContentType?.MediaType);
        var post = await host.Client.PostAsync("/api/search?q=x", null);
        Assert.Equal(HttpStatusCode.NotFound, post.StatusCode);
        Assert.Equal("application/problem+json", post.Content.Headers.ContentType?.MediaType);
        Assert.Equal(HttpStatusCode.OK, (await host.Client.GetAsync("/api/health")).StatusCode);
    }

    /// <summary>Loads like the fake model, then fails every query with a message holding a local path.</summary>
    private sealed class FailingEmbedder : IEmbedder
    {
        public const string Secret = "C:\\private\\lupa-fiscal\\data\\models";

        private readonly FakeEmbedder _inner = new();

        public string ModelId => _inner.ModelId;

        public int Dimensions => _inner.Dimensions;

        public int MaxTokens => _inner.MaxTokens;

        public int CountTokens(string text) => _inner.CountTokens(text);

        public int CountPassageTokens(string text) => _inner.CountPassageTokens(text);

        public float[] EmbedQuery(string query) => throw new InvalidOperationException($"Model file missing: {Secret}\\model.onnx");

        public IReadOnlyList<float[]> EmbedPassages(IReadOnlyList<string> passages, CancellationToken cancellationToken) =>
            _inner.EmbedPassages(passages, cancellationToken);

        public void Dispose() => _inner.Dispose();
    }
}
