namespace LupaFiscal.Api;

/// <summary>Locates the built Angular app: a directory holding index.html.</summary>
internal static class WebRoot
{
    /// <summary>
    /// The explicit directory when given (null if it has no index.html), otherwise the first of
    /// web/dist, web/dist/browser and web/dist/&lt;project&gt;/browser under the repository root.
    /// </summary>
    public static string? Find(string? explicitPath, string repositoryRoot)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            var path = Path.GetFullPath(explicitPath);
            return HasIndex(path) ? path : null;
        }

        var dist = Path.Combine(repositoryRoot, "web", "dist");
        if (!Directory.Exists(dist)) return null;
        var candidates = new[] { dist, Path.Combine(dist, "browser") }
            .Concat(Directory.EnumerateDirectories(dist).Order(StringComparer.Ordinal).Select(d => Path.Combine(d, "browser")));
        return candidates.FirstOrDefault(HasIndex);
    }

    private static bool HasIndex(string directory) => File.Exists(Path.Combine(directory, "index.html"));
}
