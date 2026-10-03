using System.Security.Cryptography;
using System.Text;
using LupaFiscal.Core.Corpus;
using LupaFiscal.Core.Crawling;

namespace LupaFiscal.Tests.Support;

/// <summary>A small synthetic extracted corpus (invented rulings modelled on the real layout).</summary>
internal static class TestCorpus
{
    /// <param name="FileName">Remote PDF file name; by default the id without its tax prefix, upper-cased.</param>
    public sealed record Ruling(string Id, string Article, DateOnly PublishedOn, string Subject, string Text, string Tax = "CIRS",
        string? FileName = null, RulingState State = RulingState.Extracted);

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

    /// <summary>
    /// Second fixture tax (CIVA). Its listing holds the same PDF as <see cref="Education"/> under another
    /// file name, a different PDF under the file name of <see cref="CapitalGains"/>, an article number
    /// that CIRS also uses (<see cref="Rental"/>: 8), and a scanned PDF without a text layer.
    /// </summary>
    public static readonly Ruling CivaEducationCopy = Education with
    {
        Id = "civa-piv_95001",
        Article = "21",
        Tax = "CIVA",
        FileName = "PIV_95001.pdf",
    };

    public static readonly Ruling CivaSameFileName = new("civa-piv_90002", "9", new DateOnly(2024, 11, 4),
        "Isenção de IVA na locação de imóveis",
        """
        INFORMAÇÃO VINCULATIVA
        FICHA DOUTRINÁRIA
        Diploma: CIVA
        Artigo: 9.º, n.º 29
        Assunto: Isenção de IVA na locação de imóveis
        Processo: 90002, com despacho de 2024-10-28, da Diretora de Serviços
        Conteúdo: A requerente pretende saber se o arrendamento de um armazém está isento de imposto sobre o valor acrescentado.
        INFORMAÇÃO
        A locação de imóveis está isenta, salvo renúncia à isenção nos termos do Decreto-Lei n.º 21/2007.
        """, Tax: "CIVA", FileName: "PIV_90002.pdf");

    public static readonly Ruling CivaDelivery = new("civa-piv_95002", "8", new DateOnly(2025, 2, 17),
        "Transmissão de bens com instalação no local",
        """
        INFORMAÇÃO VINCULATIVA
        Diploma: CIVA
        Artigo: 8.º
        Assunto: Transmissão de bens com instalação no local
        Conteúdo: As máquinas vendidas com instalação consideram-se transmitidas no local onde a instalação é efetuada.
        O imposto é liquidado pelo fornecedor à taxa normal.
        """, Tax: "CIVA");

    public static readonly Ruling CivaScanned = new("civa-piv_95003", "18", new DateOnly(2019, 5, 6),
        "Taxa reduzida em empreitadas de reabilitação", "", Tax: "CIVA", State: RulingState.ScannedSkipped);

    public static IReadOnlyList<Ruling> Civa { get; } = [CivaEducationCopy, CivaSameFileName, CivaDelivery, CivaScanned];

    /// <summary>The CIRS corpus and the CIVA corpus together.</summary>
    public static IReadOnlyList<Ruling> TwoTaxes { get; } = [.. All, .. Civa];

    public static CorpusStore Store(string dataDir) => new(Path.Combine(dataDir, "corpus"), TaxSource.Cirs);

    public static CorpusStore Store(string dataDir, string tax) =>
        new(Path.Combine(dataDir, "corpus"), TaxSource.Find(tax) ?? throw new ArgumentException($"Unknown tax {tax}."));

    /// <summary>Writes the manifest and text files of the given rulings (extracted state) into the CIRS corpus.</summary>
    public static CorpusStore Write(string dataDir, IEnumerable<Ruling> rulings)
    {
        var store = Store(dataDir);
        var manifest = new CorpusManifest { Tax = "CIRS" };
        foreach (var ruling in rulings)
        {
            manifest.Rulings.Add(Record(ruling, TaxSource.Cirs, diploma: "CIRS"));
            store.WriteText(ruling.Id, Normalized(ruling.Text));
        }
        store.SaveManifest(manifest);
        return store;
    }

    /// <summary>
    /// Writes each ruling into the corpus of its own tax (data/corpus/&lt;tax&gt;/), as the crawler
    /// does, with the SHA-256 of its PDF: rulings with the same text stand for byte-identical PDFs.
    /// Returns the stores in <see cref="TaxSource.All"/> order.
    /// </summary>
    public static IReadOnlyList<CorpusStore> WriteTaxes(string dataDir, IEnumerable<Ruling> rulings)
    {
        var stores = new List<CorpusStore>();
        foreach (var group in rulings.GroupBy(r => r.Tax).OrderBy(g => TaxSource.All.ToList().FindIndex(s => s.Code == g.Key)))
        {
            var store = Store(dataDir, group.Key);
            var manifest = new CorpusManifest { Tax = store.Source.Code };
            foreach (var ruling in group)
            {
                manifest.Rulings.Add(Record(ruling, store.Source, diploma: store.Source.Code));
                if (ruling.State == RulingState.Extracted) store.WriteText(ruling.Id, Normalized(ruling.Text));
            }
            store.SaveManifest(manifest);
            store.WriteScannedReport(manifest.Rulings);
            stores.Add(store);
        }
        return stores;
    }

    /// <summary>SHA-256 recorded for the ruling's PDF: equal for rulings that share their text.</summary>
    public static string PdfSha256(Ruling ruling) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Normalized(ruling.Text.Length > 0 ? ruling.Text : ruling.Id))));

    private static RulingRecord Record(Ruling ruling, TaxSource source, string diploma)
    {
        var baseId = ruling.Id.StartsWith(source.IdPrefix, StringComparison.Ordinal) ? ruling.Id[source.IdPrefix.Length..] : ruling.Id;
        var fileName = ruling.FileName ?? baseId.ToUpperInvariant() + ".pdf";
        return new RulingRecord
        {
            Id = ruling.Id,
            FileName = fileName,
            SourceUrl = new Uri(new Uri(TestListing.Origin), $"{source.WebPath}/Documents/{fileName}").AbsoluteUri,
            Tax = ruling.Tax,
            Diploma = diploma,
            Article = ruling.Article,
            PublishedOn = ruling.PublishedOn,
            ProcessNumber = Path.GetFileNameWithoutExtension(fileName)[4..],
            Subject = ruling.Subject,
            State = ruling.State,
            PdfSha256 = PdfSha256(ruling),
            PageCount = 1,
            TextChars = ruling.State == RulingState.Extracted ? ruling.Text.Count(char.IsLetterOrDigit) : 12,
            Reason = ruling.State == RulingState.ScannedSkipped ? "text layer has 12 letters or digits, below 200" : null,
        };
    }

    private static string Normalized(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal);
}
