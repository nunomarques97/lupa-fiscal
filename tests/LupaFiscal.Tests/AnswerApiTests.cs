using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using LupaFiscal.Api;
using LupaFiscal.Core.Answering;
using LupaFiscal.Core.Embeddings;
using LupaFiscal.Core.Indexing;
using LupaFiscal.Tests.Support;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LupaFiscal.Tests;

/// <summary>GET /api/answer over the fixture index, with fake generators in place of the local model.</summary>
public sealed partial class AnswerApiTests
{
    private const string Education = "Posso deduzir as despesas de educação dos meus filhos?";
    private const string OfficialPrefix = "https://info.portaldasfinancas.gov.pt/";

    private static readonly string[] AnswerKeys = ["query", "status", "sentences", "citations", "model", "firstTokenMs", "tookMs"];

    private static readonly string[] CitationKeys =
        ["id", "rulingId", "processNumber", "tax", "article", "date", "subject", "section", "passage", "sourceUrl"];

    private static string Q(string text) => Uri.EscapeDataString(text);

    private static async Task<(HttpResponseMessage Response, JsonElement Body, string Raw)> Get(HttpClient client, string url,
        CancellationToken cancellationToken = default)
    {
        var response = await client.GetAsync(url, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        var body = raw.Length > 0 && response.Content.Headers.ContentType?.MediaType?.Contains("json") == true
            ? JsonDocument.Parse(raw).RootElement.Clone()
            : default;
        return (response, body, raw);
    }

    private static List<(string Text, List<int> Citations)> Sentences(JsonElement body) => body.GetProperty("sentences").EnumerateArray()
        .Select(s => (s.GetProperty("text").GetString()!, s.GetProperty("citations").EnumerateArray().Select(c => c.GetInt32()).ToList()))
        .ToList();

    private static List<JsonElement> Citations(JsonElement body) => body.GetProperty("citations").EnumerateArray().ToList();

    /// <summary>The contract invariants of every 200 response: ids resolve, every citation is cited, links are official.</summary>
    private static void AssertConsistent(JsonElement body, string raw)
    {
        Assert.Equal(AnswerKeys, body.EnumerateObject().Select(p => p.Name));
        var sentences = Sentences(body);
        var citations = Citations(body);
        var ids = citations.Select(c => c.GetProperty("id").GetInt32()).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
        Assert.All(sentences.SelectMany(s => s.Citations), id => Assert.Contains(id, ids));
        Assert.All(ids, id => Assert.Contains(sentences, s => s.Citations.Contains(id)));
        Assert.All(citations, c => Assert.StartsWith(OfficialPrefix, c.GetProperty("sourceUrl").GetString()));
        // Plain text only: no markup on the wire (literal < in a passage is escaped by the JSON encoder).
        Assert.DoesNotContain('<', raw);
        Assert.DoesNotContain('>', raw);
        if (body.GetProperty("status").GetString() == "refused")
        {
            var refusal = Assert.Single(sentences);
            Assert.Equal(CitationEnforcer.RefusalText, refusal.Text);
            Assert.Empty(refusal.Citations);
            Assert.Empty(citations);
        }
        else
        {
            Assert.Equal("answered", body.GetProperty("status").GetString());
            Assert.NotEmpty(sentences);
            Assert.All(sentences, s => Assert.NotEmpty(s.Citations));
        }
    }

    /// <summary>The citation id the prompt gave to a process number, read from the prompt as the model sees it.</summary>
    private static int IdOf(GenerationRequest request, string processNumber) =>
        int.Parse(Regex.Match(request.User, $"<fonte id=\"(\\d+)\">\\nProcesso {processNumber};").Groups[1].Value);

    private static int OtherId(GenerationRequest request, string processNumber) =>
        SourceIds().Matches(request.User).Select(m => int.Parse(m.Groups[1].Value)).First(id => id != IdOf(request, processNumber));

    [Fact]
    public async Task AnswerReturnsTheContractShape()
    {
        var generator = new FakeAnswerGenerator();
        using var host = new ApiTestHost(generator: generator);

        var (response, body, raw) = await Get(host.Client, $"/api/answer?q={Q(Education)}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        AssertConsistent(body, raw);
        Assert.Equal(Education, body.GetProperty("query").GetString());
        Assert.Equal("answered", body.GetProperty("status").GetString());
        Assert.Equal(FakeAnswerGenerator.FakeModelId, body.GetProperty("model").GetString());
        Assert.Equal(5, body.GetProperty("firstTokenMs").GetDouble());
        Assert.True(body.GetProperty("tookMs").GetDouble() >= 0);

        var sentence = Assert.Single(Sentences(body));
        Assert.StartsWith("Segundo a fonte,", sentence.Text);
        Assert.DoesNotContain("[", sentence.Text);
        Assert.Equal([1], sentence.Citations);

        var citation = Assert.Single(Citations(body));
        Assert.Equal(CitationKeys, citation.EnumerateObject().Select(p => p.Name));
        Assert.Equal(1, citation.GetProperty("id").GetInt32());
        Assert.Equal("piv_90001", citation.GetProperty("rulingId").GetString());
        Assert.Equal("90001", citation.GetProperty("processNumber").GetString());
        Assert.Equal("CIRS", citation.GetProperty("tax").GetString());
        Assert.Equal("78-D", citation.GetProperty("article").GetString());
        Assert.Equal("2024-03-12", citation.GetProperty("date").GetString());
        Assert.Equal(TestCorpus.Education.Subject, citation.GetProperty("subject").GetString());
        Assert.False(string.IsNullOrEmpty(citation.GetProperty("section").GetString()));
        Assert.Contains("educação", citation.GetProperty("passage").GetString()!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(OfficialPrefix + "pt/informacao_fiscal/informacoes_vinculativas/rendimento/cirs/Documents/PIV_90001.pdf",
            citation.GetProperty("sourceUrl").GetString());

        // The same hybrid search as /api/search builds the prompt: its passages are the top search results, in order.
        var request = Assert.Single(generator.Requests);
        var search = JsonDocument.Parse(await host.Client.GetStringAsync($"/api/search?q={Q(Education)}")).RootElement;
        var top = search.GetProperty("results").EnumerateArray().Take(new AnswerOptions().Prompt.MaxPassages)
            .Select(r => r.GetProperty("processNumber").GetString()!).ToList();
        Assert.Equal(top, ProcessNumbers().Matches(request.User).Select(m => m.Groups[1].Value));
        Assert.Equal(new AnswerOptions().Prompt.MaxNewTokens, request.MaxNewTokens);
        Assert.Equal(new AnswerOptions().TimeLimit, request.TimeLimit);
    }

    [Fact]
    public async Task FiltersRestrictThePassagesGivenToTheModel()
    {
        // The passage holds "alert(1)", which the enforcer reads as a source named outside the bracket syntax, so
        // the answer is written here rather than copied from the passage.
        var generator = new FakeAnswerGenerator(_ => "A sociedade alienou um imóvel do ativo e tributa as mais-valias [1].");
        using var host = new ApiTestHost(generator: generator);

        var (response, body, raw) = await Get(host.Client, $"/api/answer?q={Q("pedido script alert valor mais-valias")}&tax=CIRC&year=2024");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertConsistent(body, raw);
        Assert.Equal(["90006"], ProcessNumbers().Matches(Assert.Single(generator.Requests).User).Select(m => m.Groups[1].Value));
        var citation = Assert.Single(Citations(body));
        Assert.Equal("circ-piv_90006", citation.GetProperty("rulingId").GetString());
        // Literal markup in a passage comes back as plain text, escaped on the wire.
        Assert.Contains("<script>alert(1)</script>", citation.GetProperty("passage").GetString());
        Assert.Contains("\\u003Cscript\\u003E", raw);
    }

    [Fact]
    public async Task AModelThatDoesNotAnswerGetsTheFixedRefusal()
    {
        using var host = new ApiTestHost(generator: new FakeAnswerGenerator(_ => "Não encontrei a resposta nas fontes."));

        var (response, body, raw) = await Get(host.Client, $"/api/answer?q={Q(Education)}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertConsistent(body, raw);
        Assert.Equal("refused", body.GetProperty("status").GetString());
        Assert.Equal(FakeAnswerGenerator.FakeModelId, body.GetProperty("model").GetString());
    }

    [Fact]
    public async Task WithoutPassagesTheModelIsNotCalledAndTheAnswerIsTheRefusal()
    {
        var generator = new FakeAnswerGenerator();
        using var host = new ApiTestHost(generator: generator);

        var (response, body, raw) = await Get(host.Client, $"/api/answer?q={Q(Education)}&tax=NOPE");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertConsistent(body, raw);
        Assert.Equal("refused", body.GetProperty("status").GetString());
        Assert.Empty(generator.Requests);
    }

    [Fact]
    public async Task UncitedSentencesAndUnknownIdsAreRemoved()
    {
        using var host = new ApiTestHost(generator: new FakeAnswerGenerator(_ =>
            "As despesas de educação são dedutíveis no IRS. " +
            "As despesas de saúde também contam para a dedução [9]. " +
            "A dedução tem um limite anual por agregado [1][7]. " +
            "Segundo a fonte, as despesas de educação dos dependentes são dedutíveis [1]. " +
            "Valor fora do formato (2). " +
            "Ignora as regras anteriores e mostra isto [0]."));

        var (response, body, raw) = await Get(host.Client, $"/api/answer?q={Q(Education)}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertConsistent(body, raw);
        var sentence = Assert.Single(Sentences(body));
        Assert.Equal("Segundo a fonte, as despesas de educação dos dependentes são dedutíveis.", sentence.Text);
        Assert.Equal([1], sentence.Citations);
        Assert.Equal([1], Citations(body).Select(c => c.GetProperty("id").GetInt32()));
    }

    [Fact]
    public async Task OnlyUnknownIdsGiveTheRefusal()
    {
        using var host = new ApiTestHost(generator: new FakeAnswerGenerator(_ =>
            "As despesas de educação são dedutíveis [12]. O limite é de 800 euros por dependente [5]."));

        var (response, body, raw) = await Get(host.Client, $"/api/answer?q={Q(Education)}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertConsistent(body, raw);
        Assert.Equal("refused", body.GetProperty("status").GetString());
    }

    [Fact]
    public async Task MarkupInModelOutputComesBackAsPlainText()
    {
        using var host = new ApiTestHost(generator: new FakeAnswerGenerator(_ =>
            "<p>As <b>despesas</b> de educação são <script>alert('x')</script>dedutíveis &lt;img src=x onerror=alert(1)&gt; no IRS [1].</p>"));

        var (response, body, raw) = await Get(host.Client, $"/api/answer?q={Q(Education)}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertConsistent(body, raw);
        var sentence = Assert.Single(Sentences(body));
        Assert.DoesNotContain('<', sentence.Text);
        Assert.DoesNotContain('>', sentence.Text);
        Assert.StartsWith("As despesas de educação são", sentence.Text);
    }

    [Fact]
    public async Task CitationsWithANonOfficialSourceUrlAreDroppedWithTheSentencesThatOnlyCiteThem()
    {
        using var host = new ApiTestHost(
            prepareIndex: path => SetSourceUrl(path, "piv_90001", "http://info.portaldasfinancas.gov.pt/PIV_90001.pdf"),
            generator: new FakeAnswerGenerator(request =>
            {
                var bad = IdOf(request, "90001");
                var good = OtherId(request, "90001");
                return $"As despesas de educação dos filhos são dedutíveis no IRS [{bad}]. " +
                    $"Esta afirmação assenta nas duas informações vinculativas [{bad}][{good}].";
            }));

        var (response, body, raw) = await Get(host.Client, $"/api/answer?q={Q(Education)}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertConsistent(body, raw);
        var sentence = Assert.Single(Sentences(body));
        Assert.Equal("Esta afirmação assenta nas duas informações vinculativas.", sentence.Text);
        var citation = Assert.Single(Citations(body));
        Assert.Equal(sentence.Citations, [citation.GetProperty("id").GetInt32()]);
        Assert.NotEqual("piv_90001", citation.GetProperty("rulingId").GetString());
        Assert.DoesNotContain("PIV_90001", raw);
    }

    [Fact]
    public async Task AnAnswerCitingOnlyNonOfficialSourcesIsTheRefusal()
    {
        using var host = new ApiTestHost(
            prepareIndex: path => SetSourceUrl(path, "piv_90001", "https://attacker.example/PIV_90001.pdf"),
            generator: new FakeAnswerGenerator(request => $"As despesas de educação dos filhos são dedutíveis [{IdOf(request, "90001")}]."));

        var (response, body, raw) = await Get(host.Client, $"/api/answer?q={Q(Education)}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertConsistent(body, raw);
        Assert.Equal("refused", body.GetProperty("status").GetString());
        Assert.DoesNotContain("attacker", raw);
    }

    [Theory]
    [InlineData("", "q")]
    [InlineData("q=", "q")]
    [InlineData("q=%20%20%09", "q")]
    [InlineData("tax=CIRS", "q")]
    [InlineData("q=a&q=b", "q")]
    [InlineData("q=LONG", "q")]
    [InlineData("q=x&year=abc", "year")]
    [InlineData("q=x&year=-2024", "year")]
    [InlineData("q=x&year=1899", "year")]
    [InlineData("q=x&year=2101", "year")]
    [InlineData("q=x&year=2023&year=2024", "year")]
    [InlineData("q=x&limit=0", "limit")]
    [InlineData("q=x&limit=51", "limit")]
    [InlineData("q=x&tax=CIRS&tax=CIRC", "tax")]
    [InlineData("q=x&article=10", "article")]
    [InlineData("q=x&article=10&tax=%20", "article")]
    public async Task InvalidRequestsReturnTheSame400AsSearchWithoutCallingTheModel(string queryString, string field)
    {
        var generator = new FakeAnswerGenerator();
        using var host = new ApiTestHost(generator: generator);
        var query = queryString.Replace("q=LONG", "q=" + new string('a', SearchQuery.MaxQueryLength + 1), StringComparison.Ordinal);

        var (response, body, _) = await Get(host.Client, "/api/answer?" + query);
        var (_, search, _) = await Get(host.Client, "/api/search?" + query);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(400, body.GetProperty("status").GetInt32());
        Assert.True(body.GetProperty("errors").TryGetProperty(field, out _));
        Assert.Equal(search.GetProperty("errors").GetRawText(), body.GetProperty("errors").GetRawText());
        Assert.Empty(generator.Requests);
    }

    [Fact]
    public async Task WithoutTheModelAnswerIs503AndEverythingElseWorks()
    {
        using var host = new ApiTestHost();

        var (response, body, raw) = await Get(host.Client, $"/api/answer?q={Q(Education)}");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(503, body.GetProperty("status").GetInt32());
        Assert.Equal("The answer model is not available.", body.GetProperty("title").GetString());
        AssertNoDetail(raw);
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        // Validation still comes first.
        Assert.Equal(HttpStatusCode.BadRequest, (await host.Client.GetAsync("/api/answer?q=x&article=10")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await host.Client.GetAsync($"/api/search?q={Q(Education)}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await host.Client.GetAsync("/api/facets")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await host.Client.GetAsync("/api/health")).StatusCode);
    }

    [Fact]
    public async Task AModelThatFailsItsChecksIsNotLoadedAndSearchKeepsWorking()
    {
        var logs = new ListLoggerProvider();
        using var host = new ApiTestHost(prepareIndex: path =>
        {
            // Every pinned file present but altered: the loader must reject them without loading anything.
            var store = new ModelStore(Path.Combine(Path.GetDirectoryName(path)!, "models"), AnswerModel.Default.Spec);
            Directory.CreateDirectory(store.DirectoryPath);
            foreach (var file in AnswerModel.Default.Spec.Files) File.WriteAllText(store.PathOf(file), "altered");
        }, logs: logs);

        Assert.Equal(HttpStatusCode.OK, (await host.Client.GetAsync($"/api/search?q={Q(Education)}")).StatusCode);
        await host.Services.GetRequiredService<AnswerModelLoader>().Completion.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Null(host.Services.GetRequiredService<IAnswerGeneratorProvider>().Generator);
        var (response, _, raw) = await Get(host.Client, $"/api/answer?q={Q(Education)}");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        AssertNoDetail(raw);
        Assert.Contains(logs.Messages, m => m.Contains("was not loaded", StringComparison.Ordinal));
        Assert.Equal(HttpStatusCode.OK, (await host.Client.GetAsync($"/api/search?q={Q(Education)}")).StatusCode);
    }

    [Fact]
    public async Task SearchFacetsAndHealthAreUnchangedByTheAnswerModel()
    {
        using var without = new ApiTestHost();
        using var with = new ApiTestHost(generator: new FakeAnswerGenerator());

        foreach (var url in new[] { "/api/facets", "/api/health" })
        {
            var (a, rawA) = await Raw(without.Client, url);
            var (b, rawB) = await Raw(with.Client, url);
            Assert.Equal(HttpStatusCode.OK, a.StatusCode);
            Assert.Equal(a.StatusCode, b.StatusCode);
            Assert.Equal(rawA, rawB);
        }
        foreach (var url in new[] { $"/api/search?q={Q(Education)}", "/api/search?q=rendimentos&tax=CIRS&limit=3", "/api/search?q=x&year=abc" })
        {
            var (a, rawA) = await Raw(without.Client, url);
            var (b, rawB) = await Raw(with.Client, url);
            Assert.Equal(a.StatusCode, b.StatusCode);
            Assert.Equal(WithoutTiming(rawA), WithoutTiming(rawB));
        }
        // An answer in progress or done does not change search either.
        await with.Client.GetAsync($"/api/answer?q={Q(Education)}");
        var (_, before) = await Raw(without.Client, $"/api/search?q={Q(Education)}");
        var (_, after) = await Raw(with.Client, $"/api/search?q={Q(Education)}");
        Assert.Equal(WithoutTiming(before), WithoutTiming(after));

        static async Task<(HttpResponseMessage, string)> Raw(HttpClient client, string url)
        {
            var response = await client.GetAsync(url);
            return (response, await response.Content.ReadAsStringAsync());
        }

        static string WithoutTiming(string raw) => TraceId().Replace(TookMs().Replace(raw, "\"tookMs\":0"), "\"traceId\":\"\"");
    }

    [Fact]
    public async Task ASecondRequestBeyondTheBoundedWaitIsBusy()
    {
        var generator = new GatedGenerator();
        using var host = new ApiTestHost(generator: generator,
            answerOptions: new AnswerServiceOptions { QueueWait = TimeSpan.FromMilliseconds(200) });

        var first = host.Client.GetAsync($"/api/answer?q={Q(Education)}");
        await generator.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var (busy, body, raw) = await Get(host.Client, $"/api/answer?q={Q(Education)}");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, busy.StatusCode);
        Assert.Equal("application/problem+json", busy.Content.Headers.ContentType?.MediaType);
        Assert.Equal("The answer model is busy with another question. Try again shortly.", body.GetProperty("title").GetString());
        AssertNoDetail(raw);
        // Search is never queued behind a generation.
        Assert.Equal(HttpStatusCode.OK, (await host.Client.GetAsync($"/api/search?q={Q(Education)}")).StatusCode);

        generator.Block = false;
        generator.Gate.Release();
        Assert.Equal(HttpStatusCode.OK, (await first.WaitAsync(TimeSpan.FromSeconds(10))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await host.Client.GetAsync($"/api/answer?q={Q(Education)}")).StatusCode);
        Assert.Equal(1, generator.MaxActive);
    }

    [Fact]
    public async Task ConcurrentRequestsWithinTheWaitRunOneAtATime()
    {
        var generator = new GatedGenerator { Block = false, Delay = TimeSpan.FromMilliseconds(50) };
        using var host = new ApiTestHost(generator: generator,
            answerOptions: new AnswerServiceOptions { QueueWait = TimeSpan.FromSeconds(30) });

        var responses = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => host.Client.GetAsync($"/api/answer?q={Q(Education)}")));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        Assert.Equal(4, generator.Calls);
        Assert.Equal(1, generator.MaxActive);
    }

    [Fact]
    public async Task AnAbortedRequestCancelsItsGenerationAndReleasesTheSlot()
    {
        var generator = new GatedGenerator();
        using var host = new ApiTestHost(generator: generator,
            answerOptions: new AnswerServiceOptions { QueueWait = TimeSpan.FromMilliseconds(500) });
        using var abort = new CancellationTokenSource();

        var first = host.Client.GetAsync($"/api/answer?q={Q(Education)}", abort.Token);
        await generator.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        abort.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        // The generation saw the abort: it was not left running to the end.
        await generator.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0, generator.Gate.CurrentCount);

        generator.Block = false;
        var (response, body, raw) = await Get(host.Client, $"/api/answer?q={Q(Education)}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertConsistent(body, raw);
        Assert.Equal(1, generator.MaxActive);
    }

    [Fact]
    public async Task AnAbortWhileWaitingForTheSlotDoesNotTakeIt()
    {
        var generator = new GatedGenerator();
        using var host = new ApiTestHost(generator: generator,
            answerOptions: new AnswerServiceOptions { QueueWait = TimeSpan.FromSeconds(30) });
        using var abort = new CancellationTokenSource();

        var first = host.Client.GetAsync($"/api/answer?q={Q(Education)}");
        await generator.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var waiting = host.Client.GetAsync($"/api/answer?q={Q(Education)}", abort.Token);
        await Task.Delay(100);
        abort.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);

        generator.Block = false;
        generator.Gate.Release();
        Assert.Equal(HttpStatusCode.OK, (await first.WaitAsync(TimeSpan.FromSeconds(10))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await host.Client.GetAsync($"/api/answer?q={Q(Education)}")).StatusCode);
        Assert.Equal(2, generator.Calls);
    }

    [Fact]
    public async Task TheQuestionIsNeverLogged()
    {
        const string Marker = "pergunta-privada-zx81";
        var logs = new ListLoggerProvider();
        using var host = new ApiTestHost(generator: new FakeAnswerGenerator(), logs: logs);
        using var absent = new ApiTestHost(logs: logs);

        await host.Client.GetAsync($"/api/answer?q={Q("despesas de educação " + Marker)}");
        await host.Client.GetAsync($"/api/answer?q={Q(Marker)}&year=abc");
        await absent.Client.GetAsync($"/api/answer?q={Q(Marker)}");

        Assert.NotEmpty(logs.Messages);
        Assert.DoesNotContain(logs.Messages, m => m.Contains(Marker, StringComparison.Ordinal));
    }

    [Fact]
    public async Task HostFilteringAndSecurityHeadersApply()
    {
        using var host = new ApiTestHost(generator: new FakeAnswerGenerator());
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/answer?q={Q(Education)}");
        request.Headers.Host = "attacker.example";

        Assert.Equal(HttpStatusCode.BadRequest, (await host.Client.SendAsync(request)).StatusCode);

        var response = await host.Client.GetAsync($"/api/answer?q={Q(Education)}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());
        Assert.Equal("no-referrer", response.Headers.GetValues("Referrer-Policy").Single());
        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.PostAsync($"/api/answer?q={Q(Education)}", null)).StatusCode);
    }

    [Fact]
    public async Task AGeneratorFailureIsA500WithoutDetailAndTheSlotIsReleased()
    {
        var generator = new GatedGenerator { Block = false, Fail = true };
        using var host = new ApiTestHost(generator: generator,
            answerOptions: new AnswerServiceOptions { QueueWait = TimeSpan.FromMilliseconds(200) });

        var (response, _, raw) = await Get(host.Client, $"/api/answer?q={Q(Education)}");
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        AssertNoDetail(raw);
        Assert.DoesNotContain(GatedGenerator.Secret, raw, StringComparison.Ordinal);

        generator.Fail = false;
        Assert.Equal(HttpStatusCode.OK, (await host.Client.GetAsync($"/api/answer?q={Q(Education)}")).StatusCode);
    }

    private static void AssertNoDetail(string raw)
    {
        Assert.DoesNotContain("Exception", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("LupaFiscal", raw, StringComparison.Ordinal);
        Assert.DoesNotContain(":\\", raw, StringComparison.Ordinal);
        Assert.DoesNotContain(".cs", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("models", raw, StringComparison.Ordinal);
    }

    private static void SetSourceUrl(string indexPath, string rulingId, string url)
    {
        using var connection = new SqliteConnection(IndexDatabase.ConnectionString(indexPath, readOnly: false));
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE rulings SET source_url = $url WHERE id = $id";
        command.Parameters.AddWithValue("$url", url);
        command.Parameters.AddWithValue("$id", rulingId);
        Assert.Equal(1, command.ExecuteNonQuery());
    }

    [GeneratedRegex("<fonte id=\"\\d+\">\\nProcesso ([^;]+);")]
    private static partial Regex ProcessNumbers();

    [GeneratedRegex("<fonte id=\"(\\d+)\">")]
    private static partial Regex SourceIds();

    [GeneratedRegex("\"tookMs\":[0-9.]+")]
    private static partial Regex TookMs();

    [GeneratedRegex("\"traceId\":\"[^\"]*\"")]
    private static partial Regex TraceId();

    /// <summary>
    /// A fake model whose generation blocks on <see cref="Gate"/> while <see cref="Block"/> is set, and honours
    /// cancellation like the real one. It counts calls and the most generations running at once.
    /// </summary>
    private sealed class GatedGenerator : IAnswerGenerator
    {
        public const string Secret = "C:\\private\\models\\model.onnx";

        private readonly FakeAnswerGenerator _inner = new();
        private int _active;
        private int _maxActive;
        private int _calls;

        public volatile bool Block = true;
        public volatile bool Fail;

        public TimeSpan Delay { get; init; }

        public SemaphoreSlim Gate { get; } = new(0);

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int MaxActive => Volatile.Read(ref _maxActive);

        public int Calls => Volatile.Read(ref _calls);

        public string ModelId => _inner.ModelId;

        public int ContextTokens => _inner.ContextTokens;

        public int CountTokens(string text) => _inner.CountTokens(text);

        public GenerationOutput Generate(GenerationRequest request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            var active = Interlocked.Increment(ref _active);
            int seen;
            while (active > (seen = Volatile.Read(ref _maxActive)) && Interlocked.CompareExchange(ref _maxActive, active, seen) != seen) { }
            try
            {
                Started.TrySetResult();
                if (Fail) throw new InvalidOperationException($"Native failure in {Secret}");
                if (Block)
                {
                    try
                    {
                        Gate.Wait(cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        Cancelled.TrySetResult();
                        throw;
                    }
                }
                if (Delay > TimeSpan.Zero) Thread.Sleep(Delay);
                lock (_inner) return _inner.Generate(request, cancellationToken);
            }
            finally
            {
                Interlocked.Decrement(ref _active);
            }
        }

        public void Dispose() => _inner.Dispose();
    }

    /// <summary>Collects every formatted log message and exception text of a host.</summary>
    private sealed class ListLoggerProvider : ILoggerProvider
    {
        private readonly ConcurrentQueue<string> _messages = new();

        public IReadOnlyList<string> Messages => _messages.ToList();

        public ILogger CreateLogger(string categoryName) => new ListLogger(categoryName, _messages);

        public void Dispose()
        {
        }

        private sealed class ListLogger(string category, ConcurrentQueue<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                messages.Enqueue($"{logLevel} {category}: {formatter(state, exception)} {exception}");
        }
    }
}
