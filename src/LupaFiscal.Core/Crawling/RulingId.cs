using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace LupaFiscal.Core.Crawling;

/// <summary>
/// Builds stable ruling ids from PDF file names. Ids only contain [a-z0-9_-], so they are safe
/// to use as cache file names; raw remote strings never reach the file system.
/// </summary>
public static partial class RulingId
{
    public const int MaxLength = 100;

    // Base length plus up to two 9-character hash suffixes and a reserved-name marker.
    private const int MaxIdLength = 128;

    private static readonly HashSet<string> WindowsReservedNames = new(StringComparer.Ordinal)
    {
        "con", "prn", "aux", "nul",
        "com1", "com2", "com3", "com4", "com5", "com6", "com7", "com8", "com9",
        "lpt1", "lpt2", "lpt3", "lpt4", "lpt5", "lpt6", "lpt7", "lpt8", "lpt9",
    };

    public static bool IsValid(string? id) =>
        id is { Length: > 0 and <= MaxIdLength } && ValidId().IsMatch(id) && !WindowsReservedNames.Contains(id);

    /// <summary>Sanitises a remote PDF file name (for example "PIV_31329.pdf") into an id ("piv_31329").</summary>
    public static string FromFileName(string fileName)
    {
        var name = fileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) ? fileName[..^4] : fileName;
        var decomposed = name.Normalize(NormalizationForm.FormKD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            var lower = char.ToLowerInvariant(c);
            builder.Append(lower is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_' ? lower : '-');
        }

        var id = Dashes().Replace(builder.ToString(), "-").Trim('-', '_');
        if (id.Length > MaxLength)
        {
            id = id[..MaxLength].TrimEnd('-', '_') + "-" + ShortHash(fileName);
        }
        if (id.Length == 0)
        {
            id = "ruling-" + ShortHash(fileName);
        }
        if (WindowsReservedNames.Contains(id))
        {
            id += "_";
        }
        return id;
    }

    /// <summary>Appends a hash of the raw file name, used when two names sanitise to the same id.</summary>
    public static string Disambiguate(string id, string fileName) => id + "-" + ShortHash(fileName);

    private static string ShortHash(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..8];

    [GeneratedRegex("^[a-z0-9_-]+$")]
    private static partial Regex ValidId();

    [GeneratedRegex("-{2,}")]
    private static partial Regex Dashes();
}
