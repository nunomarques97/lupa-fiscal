namespace LupaFiscal.Core;

/// <summary>
/// Locates the data directory: --data-dir, then LUPAFISCAL_DATA_DIR, then data/ next to the
/// repository's LupaFiscal.slnx (searched upwards from the working directory and the binaries),
/// then data/ in the working directory.
/// </summary>
public static class DataDirectory
{
    public const string EnvironmentVariable = "LUPAFISCAL_DATA_DIR";

    public static string Resolve(string? explicitPath)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath)) return Path.GetFullPath(explicitPath);
        if (Environment.GetEnvironmentVariable(EnvironmentVariable) is { Length: > 0 } fromEnvironment)
        {
            return Path.GetFullPath(fromEnvironment);
        }

        return Path.Combine(RepositoryRoot(), "data");
    }

    /// <summary>The directory holding LupaFiscal.slnx, or the working directory when there is none.</summary>
    public static string RepositoryRoot() =>
        FindRepositoryRoot(Environment.CurrentDirectory) ?? FindRepositoryRoot(AppContext.BaseDirectory) ?? Environment.CurrentDirectory;

    private static string? FindRepositoryRoot(string start)
    {
        for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "LupaFiscal.slnx"))) return directory.FullName;
        }
        return null;
    }
}
