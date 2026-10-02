using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LupaFiscal.Core.Evaluation;

/// <summary>One eval question and the rulings that answer it (any of them is a correct answer).</summary>
public sealed record EvalQuestion(string Id, string Question, string Article, IReadOnlyList<string> Expected);

/// <summary>
/// The eval question file (eval/questions.json): {"questions": [{"id", "question", "article",
/// "expected": [ruling ids]}]}. Loading validates the structure; references to the index are
/// checked separately with <see cref="UnknownRulings"/>. <see cref="Hash"/> identifies the
/// questions and their expected answers independently of JSON formatting, so a frozen set can be
/// told apart from an edited one.
/// </summary>
public sealed class EvalQuestionSet
{
    public const int MaxQuestionLength = 500;

    private EvalQuestionSet(IReadOnlyList<EvalQuestion> questions)
    {
        Questions = questions;
        var canonical = string.Join('\n', questions.Select(q =>
            string.Join('\u001f', q.Id, q.Question, q.Article, string.Join('\u001e', q.Expected))));
        Hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    public IReadOnlyList<EvalQuestion> Questions { get; }

    public string Hash { get; }

    public int ExpectedCount => Questions.Sum(q => q.Expected.Count);

    /// <summary>Reads and validates a question file. Throws <see cref="InvalidDataException"/> with the first problem found.</summary>
    public static EvalQuestionSet Load(string path)
    {
        if (!File.Exists(path)) throw new InvalidDataException($"Question file not found: {path}");
        return Parse(File.ReadAllText(path, Encoding.UTF8), path);
    }

    public static EvalQuestionSet Parse(string json, string source)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"{source}: not valid JSON ({ex.Message})");
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("questions", out var items)
                || items.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidDataException($"{source}: expected an object with a \"questions\" array.");
            }

            var questions = new List<EvalQuestion>();
            var ids = new HashSet<string>(StringComparer.Ordinal);
            var texts = new HashSet<string>(StringComparer.Ordinal);
            var index = 0;
            foreach (var item in items.EnumerateArray())
            {
                index++;
                var where = $"{source}: question #{index}";
                if (item.ValueKind != JsonValueKind.Object) throw new InvalidDataException($"{where} is not an object.");

                var id = RequiredString(item, "id", where);
                where = $"{source}: question {id}";
                var question = RequiredString(item, "question", where);
                if (question.Length > MaxQuestionLength)
                {
                    throw new InvalidDataException($"{where}: question is longer than {MaxQuestionLength} characters.");
                }
                var article = item.TryGetProperty("article", out var a) && a.ValueKind == JsonValueKind.String ? a.GetString()!.Trim() : "";

                if (!item.TryGetProperty("expected", out var expectedItems) || expectedItems.ValueKind != JsonValueKind.Array)
                {
                    throw new InvalidDataException($"{where}: \"expected\" must be an array of ruling ids.");
                }
                var expected = new List<string>();
                foreach (var e in expectedItems.EnumerateArray())
                {
                    if (e.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(e.GetString()))
                    {
                        throw new InvalidDataException($"{where}: every expected ruling id must be a non-empty string.");
                    }
                    var rulingId = e.GetString()!.Trim();
                    if (expected.Contains(rulingId, StringComparer.Ordinal))
                    {
                        throw new InvalidDataException($"{where}: expected ruling {rulingId} is listed twice.");
                    }
                    expected.Add(rulingId);
                }
                if (expected.Count == 0) throw new InvalidDataException($"{where}: lists no expected ruling.");

                if (!ids.Add(id)) throw new InvalidDataException($"{source}: question id {id} is used twice.");
                if (!texts.Add(question)) throw new InvalidDataException($"{where}: the same question text appears twice.");
                questions.Add(new EvalQuestion(id, question, article, expected));
            }

            if (questions.Count == 0) throw new InvalidDataException($"{source}: the question list is empty.");
            return new EvalQuestionSet(questions);
        }
    }

    /// <summary>Expected rulings that are not in <paramref name="known"/> (the index), in file order.</summary>
    public IReadOnlyList<(string QuestionId, string RulingId)> UnknownRulings(IReadOnlySet<string> known) =>
        Questions.SelectMany(q => q.Expected.Where(id => !known.Contains(id)).Select(id => (q.Id, id))).ToList();

    private static string RequiredString(JsonElement item, string name, string where)
    {
        if (!item.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new InvalidDataException($"{where}: \"{name}\" must be a non-empty string.");
        }
        return value.GetString()!.Trim();
    }
}
