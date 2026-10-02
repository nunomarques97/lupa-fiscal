using LupaFiscal.Core.Corpus;
using LupaFiscal.Core.Crawling;

namespace LupaFiscal.Tests.Support;

/// <summary>A small synthetic extracted corpus (invented rulings modelled on the real layout).</summary>
internal static class TestCorpus
{
    public sealed record Ruling(string Id, string Article, DateOnly PublishedOn, string Subject, string Text, string Tax = "CIRS");

    public static readonly Ruling Education = new("piv_90001", "78-D", new DateOnly(2024, 3, 12),
        "Dedução de despesas de educação e formação dos dependentes",
        """
        INFORMAÇÃO VINCULATIVA
        FICHA DOUTRINÁRIA
        Diploma: Código do Imposto sobre o Rendimento das Pessoas Singulares
        Artigo/Verba: Art.78º-D - Dedução de despesas de educação e formação
        Assunto: Dedução de despesas de educação e formação dos dependentes
        Processo: 90001, com despacho de 2024-03-01, da Diretora de Serviços
        Conteúdo: O requerente pretende saber se as propinas e o material escolar dos filhos são dedutíveis.
        FACTOS
        O requerente tem dois filhos dependentes que frequentam o ensino secundário.
        As faturas das propinas foram emitidas por estabelecimento de ensino com o NIF dos dependentes.
        1Processo: 90001

        INFORMAÇÃO VINCULATIVA
        INFORMAÇÃO
        1. Nos termos do artigo 78.º-D do Código do IRS, à coleta é dedutível um montante correspondente a 30 % das despesas de educação e formação.
        2. As despesas de educação incluem as propinas e o material escolar adquirido em estabelecimentos com o código de atividade adequado.
        CONCLUSÃO
        As despesas de educação dos dependentes são dedutíveis à coleta dentro do limite legal.
        """);

    public static readonly Ruling CapitalGains = new("piv_90002", "10", new DateOnly(2023, 6, 20),
        "Mais-valias e reinvestimento na habitação própria e permanente",
        """
        INFORMAÇÃO VINCULATIVA
        FICHA DOUTRINÁRIA
        Diploma: CIRS
        Artigo/Verba: Art.10º - Mais-valias
        Assunto: Mais-valias e reinvestimento na habitação própria e permanente
        Processo: 90002, com despacho de 2023-06-10, do Subdiretor-Geral
        Conteúdo: I - PEDIDO
        A requerente alienou o imóvel que constituía a sua habitação própria e permanente e pretende reinvestir o valor de realização.
        II. FACTOS DESCRITOS NO PEDIDO
        A venda ocorreu em 2022 e a aquisição do novo imóvel ocorrerá no prazo de trinta e seis meses.
        III. INFORMAÇÃO
        O ganho na alienação de imóvel destinado a habitação própria e permanente é excluído de tributação quando o valor de realização for reinvestido.
        """);

    public static readonly Ruling Rental = new("piv_90003", "8", new DateOnly(2022, 1, 5),
        "Rendimentos prediais pela cedência de partes comuns",
        """
        Processo: 90003/2010  1
        FICHA DOUTRINÁRIA
        Diploma: CIRS
        Artigo: 8.º, n.º 1, al. e)
        Assunto: Rendimentos prediais pela cedência de partes comuns
        Processo: 90003/2010, com despacho concordante de 2010-05-05
        Conteúdo: 1. As rendas recebidas pela cedência do uso das partes comuns de prédios em propriedade horizontal são rendimentos prediais da categoria F.
        2. Os rendimentos são imputados aos condóminos na proporção da permilagem das respetivas frações.
        """);

    public static readonly Ruling Pension = new("piv_90004", "11", new DateOnly(2021, 9, 30),
        "Pensões pagas por entidade estrangeira",
        """
        As pensões pagas por entidade estrangeira a residente em território português são rendimentos da categoria H.
        A convenção para evitar a dupla tributação determina o Estado com direito a tributar.
        """);

    public static IReadOnlyList<Ruling> All { get; } = [Education, CapitalGains, Rental, Pension];

    public static CorpusStore Store(string dataDir) => new(Path.Combine(dataDir, "corpus"), TaxSource.Cirs);

    /// <summary>Writes the manifest and text files of the given rulings (extracted state).</summary>
    public static CorpusStore Write(string dataDir, IEnumerable<Ruling> rulings)
    {
        var store = Store(dataDir);
        var manifest = new CorpusManifest { Tax = "CIRS" };
        foreach (var ruling in rulings)
        {
            manifest.Rulings.Add(new RulingRecord
            {
                Id = ruling.Id,
                FileName = ruling.Id.ToUpperInvariant() + ".pdf",
                SourceUrl = TestListing.PdfUrl(ruling.Id.ToUpperInvariant() + ".pdf"),
                Tax = ruling.Tax,
                Diploma = "CIRS",
                Article = ruling.Article,
                PublishedOn = ruling.PublishedOn,
                ProcessNumber = ruling.Id[4..],
                Subject = ruling.Subject,
                State = RulingState.Extracted,
            });
            store.WriteText(ruling.Id, ruling.Text.Replace("\r\n", "\n", StringComparison.Ordinal));
        }
        store.SaveManifest(manifest);
        return store;
    }
}
