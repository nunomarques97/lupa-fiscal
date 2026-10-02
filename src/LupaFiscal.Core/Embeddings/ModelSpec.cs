namespace LupaFiscal.Core.Embeddings;

/// <summary>One file of a pinned model: its path in the repository, local name, size and SHA-256.</summary>
public sealed record ModelFile(string RemotePath, string LocalName, long Size, string Sha256);

/// <summary>
/// A Hugging Face model pinned to one commit. Every file is verified against its SHA-256 before
/// use, so a changed upstream file or a corrupted download is never loaded.
/// </summary>
public sealed record ModelSpec(string Repository, string Revision, string DirectoryName, IReadOnlyList<ModelFile> Files)
{
    public const string ModelFileName = "model.onnx";
    public const string SentencePieceFileName = "sentencepiece.bpe.model";
    public const string VocabularyJsonFileName = "tokenizer.json";

    /// <summary>
    /// intfloat/multilingual-e5-small (MIT licence), fp32 ONNX export and its XLM-R SentencePiece
    /// files. The hashes are the Git LFS SHA-256 object ids published for this revision.
    /// </summary>
    public static ModelSpec MultilingualE5Small { get; } = new(
        "intfloat/multilingual-e5-small",
        "614241f622f53c4eeff9890bdc4f31cfecc418b3",
        "multilingual-e5-small",
        [
            new ModelFile("onnx/model.onnx", ModelFileName, 470_268_510,
                "ca456c06b3a9505ddfd9131408916dd79290368331e7d76bb621f1cba6bc8665"),
            new ModelFile("onnx/sentencepiece.bpe.model", SentencePieceFileName, 5_069_051,
                "cfc8146abe2a0488e9e2a0c56de7952f7c11ab059eca145a0a727afce0db2865"),
            new ModelFile("onnx/tokenizer.json", VocabularyJsonFileName, 17_082_730,
                "0b44a9d7b51c3c62626640cda0e2c2f70fdacdc25bbbd68038369d14ebdf4c39"),
        ]);

    /// <summary>Identifies the model and revision in the index, so a model change forces re-embedding.</summary>
    public string Id => $"{Repository}@{Revision}";

    public Uri DownloadUri(ModelFile file) =>
        new($"https://huggingface.co/{Repository}/resolve/{Revision}/{file.RemotePath}");
}
