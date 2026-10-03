using System.IO.Compression;
using System.Text;
using System.Text.Json;
using LupaFiscal.Core.Crawling;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace LupaFiscal.Tests.Support;

/// <summary>Synthetic ruling PDFs generated in memory. No real ruling PDF is ever used in tests.</summary>
internal static class TestPdfs
{
    public static readonly string[] RulingLines =
    [
        "INFORMAÇÃO VINCULATIVA",
        "Diploma: CIRS",
        "Artigo/Verba: Art. 10.º",
        "Assunto: Reinvestimento do valor de realização de habitação própria e permanente",
        "Processo: 31329, com despacho de 2026-09-22, da Diretora de Serviços",
        "I - PEDIDO",
        "A requerente pretende saber se o reinvestimento do valor de realização",
        "na aquisição de dois imóveis permite a exclusão de tributação das mais-valias.",
        "II. FACTOS DESCRITOS NO PEDIDO",
        "Em 2024 a requerente alienou o imóvel que constituía a sua habitação própria.",
        "III. INFORMAÇÃO",
        "O reinvestimento só beneficia da exclusão na parte afeta a habitação própria.",
    ];

    // PdfPig writes the standard 14 fonts without accented glyphs, so Portuguese text needs an
    // embedded TrueType font. Arial ships with Windows (the supported platform, also in CI).
    private static readonly string ArialPath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "arial.ttf");

    public static bool SupportsAccents => File.Exists(ArialPath);

    public static byte[] WithText(params string[] lines)
    {
        using var builder = new PdfDocumentBuilder();
        var font = SupportsAccents
            ? builder.AddTrueTypeFont(File.ReadAllBytes(ArialPath))
            : builder.AddStandard14Font(Standard14Font.Helvetica);
        var page = builder.AddPage(595, 842);
        var y = 800.0;
        foreach (var line in lines)
        {
            page.AddText(SupportsAccents ? line : StripAccents(line), 10, new PdfPoint(40, y), font);
            y -= 16;
        }
        return builder.Build();
    }

    /// <summary>The text a synthetic PDF is expected to contain on this machine.</summary>
    public static string Expect(string text) => SupportsAccents ? text : StripAccents(text);

    public static string StripAccents(string text) =>
        new(text.Normalize(NormalizationForm.FormD).Where(c => char.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark).ToArray());

    public static byte[] Ruling() => WithText(RulingLines);

    /// <summary>A two-page PDF with no text at all, like a blank scan.</summary>
    public static byte[] EmptyPages()
    {
        using var builder = new PdfDocumentBuilder();
        builder.AddPage(595, 842);
        builder.AddPage(595, 842);
        return builder.Build();
    }

    /// <summary>A page holding only an image, like a scanned ruling without a text layer.</summary>
    public static byte[] ImageOnly()
    {
        using var builder = new PdfDocumentBuilder();
        var page = builder.AddPage(595, 842);
        page.AddPng(TinyPng(), new PdfRectangle(40, 40, 555, 802));
        return builder.Build();
    }

    // A minimal 2x2 grey PNG, built by hand so no image file is committed.
    private static byte[] TinyPng()
    {
        using var output = new MemoryStream();
        output.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        WriteChunk(output, "IHDR", [0, 0, 0, 2, 0, 0, 0, 2, 8, 0, 0, 0, 0]);
        using var raw = new MemoryStream();
        using (var zlib = new ZLibStream(raw, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write([0, 0x80, 0x80, 0, 0x80, 0x80]);
        }
        WriteChunk(output, "IDAT", raw.ToArray());
        WriteChunk(output, "IEND", []);
        return output.ToArray();
    }

    private static void WriteChunk(Stream output, string type, byte[] data)
    {
        var typeBytes = Encoding.ASCII.GetBytes(type);
        output.Write(BigEndian((uint)data.Length));
        output.Write(typeBytes);
        output.Write(data);
        output.Write(BigEndian(Crc32([.. typeBytes, .. data])));
    }

    private static byte[] BigEndian(uint value) => [(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value];

    private static uint Crc32(byte[] data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
        }
        return ~crc;
    }
}

/// <summary>Builds listing responses in the real "listdocs" format.</summary>
internal static class TestListing
{
    public const string Origin = "https://info.portaldasfinancas.gov.pt";
    public const string DocumentsPath = "/pt/informacao_fiscal/informacoes_vinculativas/rendimento/cirs/Documents/";
    public const string RobotsUrl = Origin + "/robots.txt";

    public static string PdfUrl(string fileName) => new Uri(new Uri(Origin), DocumentsPath + fileName).AbsoluteUri;

    public static string[] Row(string href, string number, string date = "2026-09-23", string article = "010",
        string subject = "Assunto de teste") =>
    [
        $"<a href='{href}'><img src='/_layouts/15/images/icpdf.png' alt='x.pdf' /></a>",
        number,
        $"<span style='white-space: nowrap;'>{date}</span>",
        "CIRS",
        article,
        "",
        subject,
    ];

    public static string Json(params string[][] rows) => JsonSerializer.Serialize(new { data = rows, total = 0 });

    /// <summary>A listing of PIV_&lt;n&gt;.pdf rulings.</summary>
    public static string ForNumbers(params int[] numbers) =>
        Json(numbers.Select(n => Row(DocumentsPath + $"PIV_{n}.pdf", n.ToString())).ToArray());

    public static string DocumentsPathFor(TaxSource source) => source.WebPath + "/Documents/";

    public static string PdfUrlFor(TaxSource source, string fileName) =>
        new Uri(new Uri(Origin), DocumentsPathFor(source) + fileName).AbsoluteUri;

    /// <summary>A row in the column order of the library's listing fields.</summary>
    public static string[] RowFor(TaxSource source, string href, string number, string date = "2026-09-23",
        string article = "010", string subject = "Assunto de teste")
    {
        var columns = source.Columns;
        return source.Fields.Select(field =>
            field == TaxSource.DocIconField ? $"<a href='{href}'><img src='/_layouts/15/images/icpdf.png' alt='x.pdf' /></a>"
            : field == columns.ProcessNumber ? number
            : field == columns.PublishedOn ? $"<span style='white-space: nowrap;'>{date}</span>"
            : field == columns.Diploma ? source.Code
            : field == columns.Article[0] ? article
            : field == columns.Subject ? subject
            : "").ToArray();
    }

    /// <summary>A listing of PIV_&lt;n&gt;.pdf rulings of any library.</summary>
    public static string ForNumbers(TaxSource source, params int[] numbers) =>
        Json(numbers.Select(n => RowFor(source, DocumentsPathFor(source) + $"PIV_{n}.pdf", n.ToString())).ToArray());

    public static string ReadFixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name), Encoding.UTF8);
}

internal sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "lupa-fiscal-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
            // Best effort; the OS temp directory is cleaned eventually.
        }
    }
}
