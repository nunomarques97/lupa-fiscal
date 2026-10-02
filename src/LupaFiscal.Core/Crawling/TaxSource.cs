namespace LupaFiscal.Core.Crawling;

/// <summary>A tax whose rulings list can be crawled. The listing endpoint is documented in docs/research/gate0.md.</summary>
public sealed record TaxSource(string Code, Uri Origin, Uri ListingUri)
{
    public const string ListingFields =
        "DocIcon,NumeroVinculativa,Disponibilizada_x0020_em,Diploma,Artigo,N_x002e__x00ba__x002f_Al_x00ed_nea,Assunto";

    private static readonly Uri PortalOrigin = new("https://info.portaldasfinancas.gov.pt");

    public static TaxSource Cirs { get; } = new(
        "CIRS",
        PortalOrigin,
        BuildListingUri("/pt/informacao_fiscal/informacoes_vinculativas/rendimento/cirs", pageItemId: 42));

    public static IReadOnlyList<TaxSource> All { get; } = [Cirs];

    public static TaxSource? Find(string? code) =>
        All.FirstOrDefault(source => string.Equals(source.Code, code, StringComparison.OrdinalIgnoreCase));

    /// <summary>Directory name under data/corpus, derived from the fixed code, never from user input.</summary>
    public string DirectoryName => Code.ToLowerInvariant();

    // Same parameters as the SharePoint list page "Visualização por Número"; one response holds every entry.
    private static Uri BuildListingUri(string webPath, int pageItemId) => new(PortalOrigin,
        $"{webPath}/_vti_bin/portalat/docs.svc/listdocs"
        + $"?fields={ListingFields}"
        + "&sort=NumeroVinculativa:DESC"
        + $"&filter={Uri.EscapeDataString("<IsNotNull><FieldRef Name=\"ID\"></FieldRef></IsNotNull>")}"
        + $"&id={pageItemId}");
}
