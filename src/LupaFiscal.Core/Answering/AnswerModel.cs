using LupaFiscal.Core.Embeddings;

namespace LupaFiscal.Core.Answering;

/// <summary>How a model expects a system and a user message to be wrapped before generation.</summary>
public enum ChatFormat
{
    /// <summary>&lt;|im_start|&gt;role ... &lt;|im_end|&gt; (Qwen, EuroLLM).</summary>
    ChatMl,

    /// <summary>&lt;|system|&gt; ... &lt;|end|&gt;&lt;|user|&gt; ... &lt;|end|&gt;&lt;|assistant|&gt; (Phi-4).</summary>
    Phi,
}

/// <summary>
/// A local instruction model for answer synthesis: an ONNX Runtime GenAI int4 CPU build pinned like
/// the embedding model (repository, commit, sizes and SHA-256 of every file the runtime reads).
/// The SHA-256 of Git LFS files are their published object ids; the small text files were hashed
/// after download at the same revision.
/// </summary>
public sealed record AnswerModel(string Name, string DisplayName, ModelSpec Spec, ChatFormat Format, string Licence, int ContextTokens)
{
    /// <summary>Qwen3-4B-Instruct-2507 (Apache-2.0), int4 GenAI build by robinsmits (Apache-2.0).</summary>
    public static AnswerModel Qwen3_4B { get; } = new(
        "qwen3-4b",
        "Qwen3-4B-Instruct-2507 (int4, ONNX Runtime GenAI)",
        new ModelSpec(
            "robinsmits/qwen3-4b-instruct-2507-onnx-int4",
            "97d8690faf0cad28279dd10334a67c79f673a57f",
            "qwen3-4b-instruct-2507-int4",
            [
                File("genai_config.json", 1_572, "27a08d97068a3a4b7ebf1c9f35b8e4410e188f1510c5c120db9eec8c1045c26f"),
                File("model.onnx", 424_250, "7d8cde77a77dc9743be3050a2a7553d70f6488028fb7d94c0524e6869de656c0"),
                File("model.onnx.data", 2_551_480_320, "0562b2ddc15a9b812496bb709f72402c44fc8fbf2e8bc9f8f8ec25864a1411be"),
                File("tokenizer.json", 11_422_654, "aeb13307a71acd8fe81861d94ad54ab689df773318809eed3cbe794b4492dae4"),
                File("tokenizer_config.json", 5_644, "e1ff43043fd7fbe07a7656d301d3c59352984be693c69c4becd4b670f52a374b"),
                File("special_tokens_map.json", 644, "57255613bbe23c9497211ca68561ff429a51e871dbaf5a59998fa4c8f7fe168a"),
                File("added_tokens.json", 735, "79f6ec6fcc423d3a82bfac8b9033b1daac7b7bce06a5f5f441637b480cc605de"),
                File("vocab.json", 2_776_833, "ca10d7e9fb3ed18575dd1e277a2579c16d108e32f27439684afa0e10b1440910"),
                File("merges.txt", 1_671_853, "8831e4f1a044471340f7c0a83d7bd71306a5b867e95fd870f74d0c5308a904d5"),
                File("chat_template.jinja", 2_690, "0ab115e84509e86db2bb05a63edf63778ae7fe624972cdfa982b1e59053d6710"),
            ]),
        ChatFormat.ChatMl,
        "Apache-2.0",
        262_144);

    /// <summary>Phi-4-mini-instruct (MIT), Microsoft's official CPU int4 (RTN, block 32, accuracy level 4) build.</summary>
    public static AnswerModel Phi4Mini { get; } = new(
        "phi-4-mini",
        "Phi-4-mini-instruct (int4, ONNX Runtime GenAI)",
        new ModelSpec(
            "microsoft/Phi-4-mini-instruct-onnx",
            "fc04c8f93df696602fd9f300a30d1bf2e3081347",
            "phi-4-mini-instruct-int4",
            [
                PhiFile("genai_config.json", 1_520, "0fcfa1e663f2bc867f8dc62fae65dd0924f0a4d68b43d1234df742dd19171470"),
                PhiFile("model.onnx", 52_118_230, "701aa5d185b6a782bc27104a990dd5b634fa507840b7c42f7ee6f1fb812d0b83"),
                PhiFile("model.onnx.data", 4_856_573_952, "cb0267fa60befa1a4ade8c98b6d32a3d67f51abbd307c7f793f132e8d9092131"),
                PhiFile("tokenizer.json", 15_524_095, "382cc235b56c725945e149cc25f191da667c836655efd0857b004320e90e91ea"),
                PhiFile("tokenizer_config.json", 2_960, "c565326a315fbe62cda093a59d298828c8f3f823122661325f41f3ba577a7dec"),
                PhiFile("special_tokens_map.json", 587, "aff38493227d813e29fcf8406e8e90062f1f031aa47d589325e9c31d89ac7cc3"),
                PhiFile("added_tokens.json", 249, "d4f2aceb0f20b71dd1f4bcc7e052e4412946bf281840b8f83d39f259571af486"),
                PhiFile("vocab.json", 3_910_310, "6cb65a857824fa6615bb1782d95d882617a8bbce1da0317118586b36f39e98bd"),
                PhiFile("merges.txt", 2_418_348, "856ce61180bb689282eed6b3a6838bb1f438399be23aefe9d20eb379791fb4ad"),
            ]),
        ChatFormat.Phi,
        "MIT",
        131_072);

    /// <summary>EuroLLM-9B-Instruct (Apache-2.0, trained with European Portuguese), int4 GenAI build by keisuke-miyako.</summary>
    public static AnswerModel EuroLlm9B { get; } = new(
        "eurollm-9b",
        "EuroLLM-9B-Instruct (int4, ONNX Runtime GenAI)",
        new ModelSpec(
            "keisuke-miyako/EuroLLM-9B-Instruct-onnx-int4",
            "453de15dd26e3e3cd5a211db2635bc39902a5ad4",
            "eurollm-9b-instruct-int4",
            [
                File("genai_config.json", 1_458, "5086a4aacc580475659c9f9e3624ddb0cdbfab4dce0bd95e478b1d1b59ba643e"),
                File("model.onnx", 313_327, "02b88d4641fa940d78f8a3000b2c5ae7f259269bd9747a81f8e04675462d8eda"),
                File("model.onnx.data", 7_492_993_024, "25efda1238c12e353653814a38f98e172e258f4f00bfbb35b00c0742a2705b51"),
                File("tokenizer.json", 15_782_902, "e54b0a797c9ead3c0488bfbf18ae5558d5361ea9732ad263d173b02380910f73"),
                File("tokenizer_config.json", 46_980, "98e2fa08b1559c84d3e63759539c2b75edf4318d92c3e3aa795395e8a7ef27c1"),
                File("special_tokens_map.json", 557, "5a1e50e0e488ce4306a390682135546eeb6a91039212d85520a4db3e650b5c4f"),
                File("tokenizer.model", 2_408_875, "42957aec88804838805b335da1bbcce9d116024a20c6c4b56757648b9e348254"),
                File("chat_template.jinja", 295, "9850c24e4c4adc21d258566d1b1f0c1855c44256287143956631e892a38fc4de"),
            ]),
        ChatFormat.ChatMl,
        "Apache-2.0 (base model); the conversion repository states no licence of its own",
        4_096);

    public static IReadOnlyList<AnswerModel> All { get; } = [Qwen3_4B, Phi4Mini, EuroLlm9B];

    /// <summary>The chosen model (docs/research/answer-model.md): the CLI default and the model the API serves.</summary>
    public static AnswerModel Default => Qwen3_4B;

    public static AnswerModel? Find(string name) =>
        All.FirstOrDefault(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase));

    public static string Names => string.Join(", ", All.Select(m => m.Name));

    /// <summary>Model and revision, reported with every answer.</summary>
    public string Id => Spec.Id;

    private static ModelFile File(string name, long size, string sha256) => new(name, name, size, sha256);

    private static ModelFile PhiFile(string name, long size, string sha256) =>
        new("cpu_and_mobile/cpu-int4-rtn-block-32-acc-level-4/" + name, name, size, sha256);
}
