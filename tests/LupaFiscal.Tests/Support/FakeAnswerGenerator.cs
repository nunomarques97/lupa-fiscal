using System.Text.RegularExpressions;
using LupaFiscal.Core.Answering;

namespace LupaFiscal.Tests.Support;

/// <summary>
/// Stand-in for a local model: one "token" per word or punctuation mark, and an output chosen by the
/// test from the request (by default the first sentence of source [1] cited with [1]). It records
/// every request so tests can check the prompt.
/// </summary>
internal sealed partial class FakeAnswerGenerator(Func<GenerationRequest, string>? respond = null, int contextTokens = 4096) : IAnswerGenerator
{
    public const string FakeModelId = "fake/answer@1";

    public string ModelId => FakeModelId;

    public int ContextTokens => contextTokens;

    public List<GenerationRequest> Requests { get; } = [];

    public GenerationStop Stop { get; set; } = GenerationStop.EndOfText;

    public bool Disposed { get; private set; }

    public int CountTokens(string text) => Piece().Count(text);

    public GenerationOutput Generate(GenerationRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Requests.Add(request);
        var text = (respond ?? CiteFirstSource)(request);
        return new GenerationOutput(text, CountTokens(request.System) + CountTokens(request.User), CountTokens(text), 5, 20, Stop);
    }

    public void Dispose() => Disposed = true;

    /// <summary>"Segundo a fonte, ... [1]." built from the first words of source [1] in the prompt: one valid sentence.</summary>
    public static string CiteFirstSource(GenerationRequest request)
    {
        var match = FirstSource().Match(request.User);
        if (!match.Success) return "Não encontrei a resposta nas fontes.";
        // Without sentence marks, so the answer is exactly one sentence.
        var words = match.Groups["text"].Value.Replace('.', ' ').Replace('!', ' ').Replace('?', ' ')
            .Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(12);
        return $"Segundo a fonte, {string.Join(' ', words).TrimEnd(',', ';', ':')} [1].";
    }

    [GeneratedRegex(@"[\p{L}\p{N}]+|[^\s\p{L}\p{N}]")]
    private static partial Regex Piece();

    [GeneratedRegex("<fonte id=\"1\">\\n[^\\n]*\\n(?<text>[^\\n]*)\\n</fonte>")]
    private static partial Regex FirstSource();
}
