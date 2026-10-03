using LupaFiscal.Core.Embeddings;
using LupaFiscal.Core.Indexing;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace LupaFiscal.Tests.Support;

/// <summary>
/// The API in memory (TestServer) over a fixture index built with the fake embedder in a temporary
/// data directory, from three tax corpora (CIRS, CIRC and the CIVA fixture with its copy of a CIRS
/// PDF). No model, no network and no port are used.
/// </summary>
internal sealed class ApiTestHost : IDisposable
{
    public static readonly TestCorpus.Ruling SharesGains = new("piv_90005", "10", new DateOnly(2023, 11, 2),
        "Mais-valias na alienação de partes sociais",
        """
        Conteúdo: O requerente alienou ações de uma sociedade e pretende saber como apurar as mais-valias.
        INFORMAÇÃO
        As mais-valias resultantes da alienação onerosa de partes sociais são rendimentos da categoria G.
        """);

    /// <summary>Another tax, and literal markup in the text, which must come back as plain text.</summary>
    public static readonly TestCorpus.Ruling CorporateGains = new("circ-piv_90006", "10", new DateOnly(2024, 5, 1),
        "Mais-valias de sociedades",
        """
        Conteúdo: A sociedade alienou um imóvel do ativo e pretende saber como tributar as mais-valias.
        INFORMAÇÃO
        O texto do pedido incluía <script>alert(1)</script> e o valor <b>1000</b> & mais.
        """, Tax: "CIRC");

    public static IReadOnlyList<TestCorpus.Ruling> Corpus { get; } = [.. TestCorpus.All, SharesGains, CorporateGains, .. TestCorpus.Civa];

    /// <summary>
    /// The distinct indexed rulings: the CIVA copy of <see cref="TestCorpus.Education"/> is stored as
    /// that CIRS ruling, and the scanned CIVA PDF is not indexed.
    /// </summary>
    public static IReadOnlySet<string> RulingIds { get; } = Corpus
        .Where(r => r.State == Core.Corpus.RulingState.Extracted && r != TestCorpus.CivaEducationCopy)
        .Select(r => r.Id).ToHashSet();

    private readonly TempDirectory _temp = new();
    private readonly WebApplicationFactory<LupaFiscal.Api.Program> _factory;

    /// <param name="prepareIndex">Runs on the built index file before the API opens it.</param>
    /// <param name="embedder">Replaces the model; the fake embedder by default.</param>
    /// <param name="webRoot">The --web-root setting; by default a directory without an app.</param>
    public ApiTestHost(Action<string>? prepareIndex = null, IEmbedder? embedder = null, string? webRoot = null)
    {
        var stores = TestCorpus.WriteTaxes(_temp.Path, Corpus);
        using (var database = IndexDatabase.OpenForWrite(IndexPath))
        {
            new IndexBuilder(database, new FakeEmbedder(), new ChunkingOptions(), TextWriter.Null).Build(stores, CancellationToken.None);
        }
        prepareIndex?.Invoke(IndexPath);

        var noApp = Path.Combine(_temp.Path, "no-web-app");
        _factory = new WebApplicationFactory<LupaFiscal.Api.Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("data-dir", _temp.Path);
            builder.UseSetting("web-root", webRoot ?? noApp);
            builder.ConfigureTestServices(services => services.AddSingleton(embedder ?? new FakeEmbedder()));
        });
        Client = _factory.CreateClient();
    }

    public string IndexPath => Path.Combine(_temp.Path, "lupa-fiscal.db");

    public HttpClient Client { get; }

    public void Dispose()
    {
        Client.Dispose();
        _factory.Dispose();
        _temp.Dispose();
    }
}
