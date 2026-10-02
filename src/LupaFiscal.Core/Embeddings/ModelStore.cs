using System.Security.Cryptography;

namespace LupaFiscal.Core.Embeddings;

/// <summary>Raised when a model file is missing or does not match its pinned SHA-256.</summary>
public sealed class ModelIntegrityException(string message) : Exception(message);

/// <summary>Local copy of a pinned model under data/models/&lt;name&gt;/.</summary>
public sealed class ModelStore(string modelsRoot, ModelSpec spec)
{
    public ModelSpec Spec { get; } = spec;

    public string DirectoryPath { get; } = Path.GetFullPath(Path.Combine(modelsRoot, spec.DirectoryName));

    public string PathOf(ModelFile file) => Path.Combine(DirectoryPath, file.LocalName);

    public string PathOf(string localName) =>
        PathOf(Spec.Files.Single(f => string.Equals(f.LocalName, localName, StringComparison.Ordinal)));

    public bool AllFilesPresent => Spec.Files.All(f => File.Exists(PathOf(f)));

    /// <summary>True when the file exists with the pinned size and SHA-256.</summary>
    public bool IsValid(ModelFile file)
    {
        var path = PathOf(file);
        if (!File.Exists(path) || new FileInfo(path).Length != file.Size) return false;
        return string.Equals(Sha256Of(path), file.Sha256, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Checks every file before use; throws without loading anything if one is missing or altered.</summary>
    public void VerifyAll()
    {
        foreach (var file in Spec.Files)
        {
            if (!File.Exists(PathOf(file)))
            {
                throw new ModelIntegrityException($"Model file missing: {PathOf(file)}. Run: model download");
            }
            if (!IsValid(file))
            {
                throw new ModelIntegrityException(
                    $"Model file {PathOf(file)} does not match its pinned SHA-256; it was not used. Run: model download");
            }
        }
    }

    public static string Sha256Of(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }
}
