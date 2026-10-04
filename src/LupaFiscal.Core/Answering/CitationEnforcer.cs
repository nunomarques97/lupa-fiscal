using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace LupaFiscal.Core.Answering;

/// <summary>A shown sentence: plain text without its markers, and the citation ids it carries (in order, distinct).</summary>
public sealed record AnswerSentence(string Text, IReadOnlyList<int> Citations);

/// <summary>
/// The answer after enforcement. <see cref="RawSentences"/> and <see cref="ValidRawSentences"/> count the
/// model's sentences before filtering, to measure how often the model cites correctly by itself.
/// </summary>
public sealed record EnforcedAnswer(bool Refused, IReadOnlyList<AnswerSentence> Sentences, int RawSentences, int ValidRawSentences)
{
    /// <summary>The shown text: each sentence followed by its markers, or the fixed refusal.</summary>
    public string Text => Refused
        ? CitationEnforcer.RefusalText
        : string.Join(' ', Sentences.Select(s => s.Text + " " + string.Concat(s.Citations.Select(id => $"[{id}]"))));
}

/// <summary>
/// Enforces strict citations on model output, in code. The output is reduced to plain text (HTML,
/// entities and Markdown removed) and split into sentences; Portuguese legal abbreviations such as
/// "art.", "n.º" or "al." do not end a sentence. A sentence is kept only when it carries at least one
/// citation, every id it cites was provided in the prompt, it has no malformed bracket nor a source named
/// another way (such as "(1)" or "(Fonte 2)"), it does not contain a rejected sentence (the prompt's format
/// example), and it has at least <see cref="MinWords"/> words. A repeated sentence is shown once. When no sentence is
/// kept the answer is the fixed refusal.
/// </summary>
public static partial class CitationEnforcer
{
    /// <summary>
    /// Shown instead of an answer when no sentence survives enforcement. The goal text that defined this
    /// wording was truncated after "(Nao"; this is the chosen European Portuguese wording, kept in this one constant.
    /// </summary>
    public const string RefusalText = "Não encontrei nas informações vinculativas pesquisadas uma resposta suportada por fontes.";

    /// <summary>Fewer words than this is a label such as "Fontes:", not a sentence.</summary>
    public const int MinWords = 3;

    private const int MaxCitationId = 999;

    /// <summary>Words that end with a period without ending a sentence (compared in lower case, without the period).</summary>
    private static readonly HashSet<string> Abbreviations = new(StringComparer.Ordinal)
    {
        "art", "arts", "artº", "n", "nº", "nr", "núm", "num", "al", "als", "alín", "alin", "cfr", "cf", "p", "pp", "pág", "págs",
        "ss", "seg", "segs", "proc", "dec", "dl", "sr", "sra", "srs", "dr", "dra", "lda", "s.a", "i.e", "e.g", "ex", "v.g", "vol",
        "cap", "ref", "doc", "aprox", "máx", "mín", "inc", "sec", "nos", "n.os", "ob", "cit", "op", "id", "ibid", "c.f",
    };

    /// <param name="output">Raw model output; null or empty gives the refusal.</param>
    /// <param name="providedIds">The ids labelled in the prompt; any other id invalidates its sentence.</param>
    /// <param name="complete">False when generation stopped at a limit: an unfinished last sentence is then dropped.</param>
    /// <param name="rejected">Word sequences (case and punctuation ignored) that make a sentence containing them invalid.</param>
    public static EnforcedAnswer Enforce(string? output, IReadOnlySet<int> providedIds, bool complete = true,
        IReadOnlyCollection<string>? rejected = null)
    {
        var rejectedKeys = (rejected ?? []).Select(WordKey).Where(k => k.Length > 2).ToList();
        var text = PlainText(output ?? string.Empty);
        var segments = Split(text);
        if (!complete && segments.Count > 0 && !EndsSentence(segments[^1])) segments.RemoveAt(segments.Count - 1);

        var kept = new List<AnswerSentence>();
        int raw = 0, valid = 0;
        foreach (var segment in segments)
        {
            var (sentence, ok) = Check(segment, providedIds);
            if (sentence is null) continue;
            raw++;
            if (!ok || rejectedKeys.Any(WordKey(sentence.Text).Contains)) continue;
            valid++;
            // A model stuck in a loop repeats a sentence: it is valid, but shown once.
            if (!kept.Any(k => WordKey(k.Text) == WordKey(sentence.Text))) kept.Add(sentence);
        }
        return new EnforcedAnswer(kept.Count == 0, kept, raw, valid);
    }

    /// <summary>Decodes entities and removes HTML tags, reasoning blocks and Markdown, keeping line breaks.</summary>
    internal static string PlainText(string output)
    {
        var text = ThinkBlock().Replace(output, " ");
        text = WebUtility.HtmlDecode(text);
        text = Tag().Replace(text, " ");
        text = text.Replace("<", " ", StringComparison.Ordinal).Replace(">", " ", StringComparison.Ordinal);
        text = MarkdownLink().Replace(text, "$1");
        text = LinePrefix().Replace(text, string.Empty);
        text = Emphasis().Replace(text, string.Empty);
        var builder = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (c is '\n') builder.Append('\n');
            else if (char.IsControl(c) || char.GetUnicodeCategory(c) is System.Globalization.UnicodeCategory.Format) builder.Append(' ');
            else builder.Append(c);
        }
        // A citation group written after the full stop belongs to the sentence it follows: "... dedutíveis. [1]".
        return TrailingGroups().Replace(builder.ToString(), "${groups}${punct}");
    }

    /// <summary>Sentences in order: split at line breaks and at a terminal mark followed by space and a sentence start.</summary>
    internal static List<string> Split(string text)
    {
        var sentences = new List<string>();
        foreach (var line in text.Split('\n'))
        {
            var start = 0;
            for (var i = 0; i < line.Length; i++)
            {
                if (line[i] is not ('.' or '!' or '?' or '…')) continue;
                var end = i + 1;
                while (end < line.Length && line[end] is '.' or '!' or '?' or '…' or '"' or '\'' or '»' or '”' or ')') end++;
                if (end < line.Length && !char.IsWhiteSpace(line[end])) continue;
                var next = end;
                while (next < line.Length && char.IsWhiteSpace(line[next])) next++;
                if (next < line.Length && !StartsSentence(line[next])) continue;
                if (line[i] == '.' && IsAbbreviation(line, i)) continue;
                Add(sentences, line[start..end]);
                start = end;
                i = end - 1;
            }
            Add(sentences, line[start..]);
        }
        return sentences;
    }

    private static void Add(List<string> sentences, string segment)
    {
        var trimmed = segment.Trim();
        if (trimmed.Length > 0) sentences.Add(trimmed);
    }

    private static bool StartsSentence(char c) =>
        char.IsUpper(c) || char.IsDigit(c) || c is '"' or '«' or '“' or '(' or '[' or '\'';

    /// <summary>True when the word before the period at <paramref name="dot"/> is a known abbreviation or a single letter.</summary>
    private static bool IsAbbreviation(string line, int dot)
    {
        var start = dot;
        while (start > 0 && (char.IsLetter(line[start - 1]) || line[start - 1] is '.' or 'º' or 'ª')) start--;
        var word = line[start..dot].ToLowerInvariant().TrimStart('.');
        if (word.Length == 0) return false;
        // A lone letter is an initial ("J. Silva"), unless it is an article suffix ("78.º-D.").
        if (word.Length == 1 && char.IsLetter(word[0])) return start == 0 || line[start - 1] != '-';
        return Abbreviations.Contains(word);
    }

    /// <summary>The words in lower case between spaces, so a containment test matches whole words only.</summary>
    private static string WordKey(string text) =>
        " " + string.Join(' ', Words().Matches(text).Select(m => m.Value.ToLowerInvariant())) + " ";

    private static bool EndsSentence(string segment)
    {
        var trimmed = CitationGroup().Replace(segment, string.Empty).TrimEnd(' ', '"', '\'', '»', '”', ')');
        return trimmed.Length > 0 && trimmed[^1] is '.' or '!' or '?' or '…';
    }

    /// <summary>The sentence (null when it has no words) and whether it may be shown.</summary>
    private static (AnswerSentence? Sentence, bool Valid) Check(string segment, IReadOnlySet<int> providedIds)
    {
        var ids = new List<int>();
        var wellFormed = true;
        var rest = CitationGroup().Replace(segment, match =>
        {
            foreach (var part in match.Groups["ids"].Value.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries))
            {
                if (int.TryParse(part, out var id) && id is > 0 and <= MaxCitationId)
                {
                    if (!ids.Contains(id)) ids.Add(id);
                }
                else
                {
                    wellFormed = false;
                }
            }
            return " ";
        });
        // A stray bracket, or a source named any other way ("(1)", "(Fonte 2)"), is an ambiguous citation: not shown.
        if (rest.Contains('[') || rest.Contains(']') || PseudoCitation().IsMatch(rest)) wellFormed = false;

        var words = Words().Matches(rest).Count;
        if (words == 0) return (null, false);
        var text = Spaces().Replace(rest, " ").Trim();
        text = SpaceBeforePunctuation().Replace(text, "$1");
        if (text.Length > 0 && text[^1] is not ('.' or '!' or '?' or '…')) text = text.TrimEnd(':', ',', ';', ' ') + ".";

        var valid = wellFormed && ids.Count > 0 && ids.All(providedIds.Contains) && words >= MinWords;
        return (new AnswerSentence(text, ids), valid);
    }

    [GeneratedRegex(@"<think>.*?(</think>|$)", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex ThinkBlock();

    [GeneratedRegex(@"</?[A-Za-z!/][^<>]{0,500}>")]
    private static partial Regex Tag();

    [GeneratedRegex(@"\[([^\[\]\n]+)\]\((?:https?|mailto):[^)\s]*\)")]
    private static partial Regex MarkdownLink();

    [GeneratedRegex(@"^[ \t]*(?:#{1,6}[ \t]+|>[ \t]*|[-*•+][ \t]+|\d{1,2}[.)][ \t]+)", RegexOptions.Multiline)]
    private static partial Regex LinePrefix();

    [GeneratedRegex(@"\*{1,3}|_{2,3}|`+|~~")]
    private static partial Regex Emphasis();

    [GeneratedRegex(@"(?<punct>[.!?…]+)(?<groups>(?:[ \t]*\[[^\[\]\n]{0,40}\])+)")]
    private static partial Regex TrailingGroups();

    /// <summary>A bracket group that looks like a citation: digits separated by commas, semicolons or spaces.</summary>
    [GeneratedRegex(@"\[(?<ids>[\s\d,;]*\d[\s\d,;]*)\]")]
    private static partial Regex CitationGroup();

    /// <summary>"(1)", "(1, 2)", "(1 e 3)", "(Fonte)", "(fontes 1 e 2)": a source cited outside the bracket syntax.</summary>
    [GeneratedRegex(@"\(\s*\d{1,3}(?:\s*(?:[,;]|e)\s*\d{1,3})*\s*\)|\(\s*fontes?\b[^()]{0,40}\)", RegexOptions.IgnoreCase)]
    private static partial Regex PseudoCitation();

    [GeneratedRegex(@"[\p{L}\p{N}]+")]
    private static partial Regex Words();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    [GeneratedRegex(@"\s+([.,;:!?…)])")]
    private static partial Regex SpaceBeforePunctuation();
}
