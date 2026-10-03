namespace LupaFiscal.Core.Crawling;

/// <summary>
/// Listing columns that hold each ruling field. A null field is absent from that library's listing.
/// <see cref="Article"/> lists candidate columns in order; the first non-empty one wins.
/// </summary>
public sealed record ListingColumns(
    string ProcessNumber,
    string PublishedOn,
    string? Diploma,
    IReadOnlyList<string> Article,
    string? Paragraph,
    string Subject)
{
    public IEnumerable<string> All() =>
        new[] { ProcessNumber, PublishedOn, Diploma, Paragraph, Subject }.Concat(Article).OfType<string>();
}

/// <summary>
/// A library of binding rulings whose listing can be crawled. Endpoints, page item ids, fields and
/// per-library field differences come from docs/research/sources.md (CIRS: docs/research/gate0.md).
/// Every library is read with one unpaged "listdocs" request (no library enables the paged search).
/// </summary>
public sealed record TaxSource
{
    public const string DocIconField = "DocIcon";

    // SharePoint internal field names used by the listings.
    private const string Number = "NumeroVinculativa";
    private const string VincNumber = "Vinc_x002e__x0020_n_x002e__x00ba_";
    private const string Published = "Disponibilizada_x0020_em";
    private const string Date = "Data";
    private const string Diploma = "Diploma";
    private const string Article = "Artigo";
    private const string ArticleZero = "Artigo0";
    private const string PreviousArticle = "Anterior_x0020_Artigo";
    private const string Paragraph = "N_x002e__x00ba__x002f_Al_x00ed_nea";
    private const string ParagraphNoDot = "N_x00ba__x002f_Al_x00ed_nea";
    private const string Subject = "Assunto";
    private const string Summary = "Assunto_Resumo";

    private const string RulingsRoot = "/pt/informacao_fiscal/informacoes_vinculativas";
    private const string UnfilteredCaml = "<IsNotNull><FieldRef Name=\"ID\"></FieldRef></IsNotNull>";
    private const string DefaultSort = $"{Number}:DESC,{Published}:DESC,{Article}:ASC";

    private static readonly Uri PortalOrigin = new("https://info.portaldasfinancas.gov.pt");

    private static readonly ListingColumns Standard = new(Number, Published, Diploma, [Article], Paragraph, Subject);

    private TaxSource(string code, string webPath, int pageItemId, string[] fields, string sort, ListingColumns columns,
        string idPrefix)
    {
        if (!fields.Contains(DocIconField) || columns.All().Any(field => !fields.Contains(field)))
        {
            throw new ArgumentException($"Listing fields of {code} do not cover its column map.", nameof(fields));
        }
        Code = code;
        Origin = PortalOrigin;
        WebPath = RulingsRoot + webPath;
        PageItemId = pageItemId;
        Fields = fields;
        Sort = sort;
        Columns = columns;
        IdPrefix = idPrefix;
        ListingUri = new Uri(PortalOrigin,
            $"{WebPath}/_vti_bin/portalat/docs.svc/listdocs"
            + $"?fields={string.Join(',', fields)}"
            + $"&sort={sort}"
            + $"&filter={Uri.EscapeDataString(UnfilteredCaml)}"
            + $"&id={pageItemId}");
    }

    public string Code { get; }

    public Uri Origin { get; }

    /// <summary>SharePoint web path of the library, for example ".../rendimento/cirs".</summary>
    public string WebPath { get; }

    public int PageItemId { get; }

    /// <summary>Requested listing fields; each response row has one column per field, in this order.</summary>
    public IReadOnlyList<string> Fields { get; }

    public string Sort { get; }

    public ListingColumns Columns { get; }

    /// <summary>
    /// Prefix of the ruling ids of this library. Empty for CIRS, so v0.1 ids never change; "&lt;code&gt;-"
    /// for every other library, because the same file name can appear in two libraries.
    /// </summary>
    public string IdPrefix { get; }

    /// <summary>Unfiltered listing of the library: one response holds every entry.</summary>
    public Uri ListingUri { get; }

    /// <summary>Directory name under data/corpus, derived from the fixed code, never from user input.</summary>
    public string DirectoryName => Code.ToLowerInvariant();

    // v0.1 endpoint, unchanged: the numbered view's fields and sort.
    public static TaxSource Cirs { get; } = new("CIRS", "/rendimento/cirs", 42,
        [DocIconField, Number, Published, Diploma, Article, Paragraph, Subject], $"{Number}:DESC", Standard, idPrefix: "");

    /// <summary>Every supported library, in display-precedence order (CIRS first, then the landing page order).</summary>
    public static IReadOnlyList<TaxSource> All { get; } =
    [
        Cirs,
        Library("CIRC", "/rendimento/circ", 43,
            [DocIconField, Number, Published, Diploma, Article, Subject, PreviousArticle, Summary, Paragraph], DefaultSort, Standard),
        Library("DSRI", "/rendimento/DSRI", 27,
            [DocIconField, Number, Published, Diploma, Article, Subject, Paragraph], DefaultSort, Standard),
        Library("EBF", "/beneficios_fiscais", 35,
            [DocIconField, Number, Published, Diploma, ArticleZero, Subject, ParagraphNoDot, Summary], $"{Number}:DESC",
            Standard with { Article = [ArticleZero], Paragraph = ParagraphNoDot }),
        Library("CIMI", "/patrimonio/cimi", 42,
            [DocIconField, Number, Published, Diploma, Article, Subject, ParagraphNoDot, Summary], DefaultSort,
            Standard with { Paragraph = ParagraphNoDot }),
        // The numbered views of CIMT and CIUC filter on their own diploma; the unfiltered call also
        // returns the complementary-legislation rulings (CIMT: 221 instead of 188).
        Library("CIMT", "/patrimonio/cimt", 40,
            [DocIconField, Number, Published, Diploma, Article, Subject, Paragraph, Summary], $"{Number}:DESC", Standard),
        Library("CIUC", "/patrimonio/ciuc", 30,
            [DocIconField, Number, Published, Diploma, Article, Subject, Paragraph, Summary], $"{Number}:DESC", Standard),
        Library("SELO", "/patrimonio/selo", 37,
            [DocIconField, Number, Published, Diploma, Article, Subject, Paragraph], $"{Number}:DESC", Standard),
        // CIVA shows "Anterior Artigo" as its article; "Artigo" is almost always empty.
        Library("CIVA", "/despesa/civa", 51,
            [DocIconField, VincNumber, Date, Diploma, PreviousArticle, Subject, Paragraph, Summary, Article],
            $"{VincNumber}:DESC,{Date}:DESC",
            new ListingColumns(VincNumber, Date, Diploma, [PreviousArticle, Article], Paragraph, Subject)),
        Library("RITI", "/despesa/riti", 32,
            [DocIconField, Published, VincNumber, Article, Subject, ParagraphNoDot, Summary],
            $"{Published}:DESC,{VincNumber}:DESC,{Article}:ASC",
            new ListingColumns(VincNumber, Published, Diploma: null, [Article], ParagraphNoDot, Subject)),
        Library("LGT", "/Justica_Tributaria/LGT", 1,
            [DocIconField, Article, Paragraph, Subject, Published, Number, Diploma],
            $"{Diploma}:ASC,{Article}:ASC,{Paragraph}:ASC", Standard),
        // CESE and CSB: "Artigo" is not shown but comes with the sort; the trailing comma is as served.
        Library("CESE", "/Contribuicoes_extraordinarias/CESE", 1,
            [DocIconField, Subject, Published, Number, Article], $"{Article}:ASC,",
            new ListingColumns(Number, Published, Diploma: null, [Article], Paragraph: null, Subject)),
        Library("CSB", "/Contribuicoes_extraordinarias/CSB", 1,
            [DocIconField, Subject, Published, Number, Article], $"{Article}:ASC,",
            new ListingColumns(Number, Published, Diploma: null, [Article], Paragraph: null, Subject)),
    ];

    public static TaxSource? Find(string? code) =>
        All.FirstOrDefault(source => string.Equals(source.Code, code, StringComparison.OrdinalIgnoreCase));

    private static TaxSource Library(string code, string webPath, int pageItemId, string[] fields, string sort,
        ListingColumns columns) =>
        new(code, webPath, pageItemId, fields, sort, columns, idPrefix: code.ToLowerInvariant() + "-");
}
