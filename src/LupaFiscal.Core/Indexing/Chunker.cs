using System.Text.RegularExpressions;
using LupaFiscal.Core.Embeddings;

namespace LupaFiscal.Core.Indexing;

public sealed record ChunkingOptions
{
    /// <summary>Model tokens per chunk, including the "passage: " prefix and special tokens.</summary>
    public int MaxTokens { get; init; } = 512;

    /// <summary>
    /// Each chunk after the first of a section repeats the last whole sentences of the previous
    /// chunk, up to this many model tokens.
    /// </summary>
    public int OverlapTokens { get; init; } = 64;

    public string Describe() => $"chunker v{Chunker.Version}, max {MaxTokens} tokens, overlap {OverlapTokens}";
}

/// <summary>A passage of one ruling: [Start, End) offsets into the normalised body, and its text.</summary>
public sealed record Chunk(int Ordinal, string Section, int Start, int End, string Text, int TokenCount);

/// <summary>
/// Splits a ruling body into chunks that never cross a section boundary and fit the model window.
/// Sections are split into sentences, which are packed greedily up to the token budget. The next
/// chunk starts with the last whole sentences of the previous one (up to the overlap budget).
/// A sentence longer than the overlap budget is first cut at word boundaries into parts that fit
/// it, so an over-long sentence never blocks the window or the overlap.
/// </summary>
public sealed partial class Chunker
{
    /// <summary>Bump when the algorithm changes, so every ruling is chunked and embedded again.</summary>
    public const int Version = 1;

    private readonly IEmbedder _counter;
    private readonly ChunkingOptions _options;
    private readonly int _textBudget;

    public Chunker(IEmbedder counter, ChunkingOptions options)
    {
        _counter = counter;
        _options = options;
        if (options.MaxTokens > counter.MaxTokens)
        {
            throw new ArgumentOutOfRangeException(nameof(options), $"MaxTokens exceeds the model window of {counter.MaxTokens}.");
        }
        _textBudget = options.MaxTokens - counter.CountPassageTokens("");
        if (options.OverlapTokens < 0 || options.OverlapTokens * 2 > _textBudget)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "OverlapTokens must be between 0 and half the text budget.");
        }
    }

    public ChunkingOptions Options => _options;

    public IReadOnlyList<Chunk> Split(string body)
    {
        var chunks = new List<Chunk>();
        foreach (var section in RulingSections.Split(body))
        {
            var units = Units(body, section.Start, section.End);
            var i = 0;
            while (i < units.Count)
            {
                var j = i;
                var tokens = 0;
                while (j < units.Count && tokens + units[j].Tokens <= _textBudget)
                {
                    tokens += units[j].Tokens;
                    j++;
                }
                if (j == i) j = i + 1; // cannot happen with units within budget; guarantees progress

                j = EmitFitting(chunks, body, section.Name, units, i, j);
                if (j >= units.Count) break;

                var next = j;
                var overlap = 0;
                while (next - 1 > i && overlap + units[next - 1].Tokens <= _options.OverlapTokens)
                {
                    overlap += units[next - 1].Tokens;
                    next--;
                }
                i = next;
            }
        }
        return chunks;
    }

    /// <summary>
    /// Emits units [i, j) after an exact count of the full passage input. Sums of separately
    /// counted units are exact when they are separated by whitespace, but a unit cut inside a
    /// word may count differently, so the last unit is dropped until the chunk fits.
    /// Returns the end of the emitted range.
    /// </summary>
    private int EmitFitting(List<Chunk> chunks, string body, string section, List<Unit> units, int i, int j)
    {
        while (true)
        {
            var start = units[i].Start;
            var end = units[j - 1].End;
            var text = body[start..end];
            var total = _counter.CountPassageTokens(text);
            if (total <= _options.MaxTokens)
            {
                chunks.Add(new Chunk(chunks.Count, section, start, end, text, total));
                return j;
            }
            if (j - 1 > i)
            {
                j--;
                continue;
            }

            // A single unit that still does not fit: keep the longest prefix that does.
            var low = 1;
            var high = text.Length;
            while (low < high)
            {
                var mid = (low + high + 1) / 2;
                if (_counter.CountPassageTokens(text[..mid]) <= _options.MaxTokens) low = mid; else high = mid - 1;
            }
            units.Insert(j, new Unit(start + low, end, _counter.CountTokens(body[(start + low)..end])));
            units[i] = new Unit(start, start + low, _counter.CountTokens(text[..low]));
        }
    }

    private List<Unit> Units(string body, int start, int end)
    {
        var units = new List<Unit>();
        var sentenceStart = start;
        foreach (Match boundary in SentenceEnd().Matches(body[start..end]))
        {
            AddSentence(units, body, sentenceStart, start + boundary.Index);
            sentenceStart = start + boundary.Index + boundary.Length;
        }
        AddSentence(units, body, sentenceStart, end);
        return units;
    }

    private void AddSentence(List<Unit> units, string body, int start, int end)
    {
        while (start < end && char.IsWhiteSpace(body[start])) start++;
        while (end > start && char.IsWhiteSpace(body[end - 1])) end--;
        if (end <= start) return;

        var tokens = _counter.CountTokens(body[start..end]);
        if (tokens <= _options.OverlapTokens || _options.OverlapTokens == 0 && tokens <= _textBudget)
        {
            units.Add(new Unit(start, end, tokens));
            return;
        }

        // Over-long sentence: parts of whole words, each within the overlap budget.
        var limit = Math.Max(_options.OverlapTokens, 1);
        int? partStart = null;
        var partEnd = start;
        var partTokens = 0;
        foreach (Match word in Word().Matches(body[start..end]))
        {
            var wordStart = start + word.Index;
            var wordEnd = wordStart + word.Length;
            var wordTokens = _counter.CountTokens(word.Value);
            if (partStart is { } open && partTokens + wordTokens > limit)
            {
                units.Add(new Unit(open, partEnd, partTokens));
                partStart = null;
                partTokens = 0;
            }
            partStart ??= wordStart;
            partEnd = wordEnd;
            partTokens += wordTokens;
        }
        if (partStart is { } last) units.Add(new Unit(last, partEnd, partTokens));
    }

    private readonly record struct Unit(int Start, int End, int Tokens);

    // End of a sentence or clause: . ; : ! ? followed by whitespace.
    [GeneratedRegex(@"(?<=[.;:!?])\s+")]
    private static partial Regex SentenceEnd();

    [GeneratedRegex(@"\S+")]
    private static partial Regex Word();
}
