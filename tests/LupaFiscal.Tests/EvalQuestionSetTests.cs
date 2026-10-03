using System.Text.Json;
using LupaFiscal.Core.Evaluation;
using LupaFiscal.Core.Search;

namespace LupaFiscal.Tests;

public sealed class EvalQuestionSetTests
{
    private const string Valid = """
        {"questions": [
          {"id": "q01", "article": "78-D", "question": "Posso deduzir as propinas?", "expected": ["piv_90001"]},
          {"id": "q02", "article": "10", "question": "Vendi a casa, pago mais-valias?", "expected": ["piv_90002", "piv_90003"]}
        ]}
        """;

    [Fact]
    public void ParsesQuestionsAndFindsUnknownRulings()
    {
        var set = EvalQuestionSet.Parse(Valid, "test");

        Assert.Equal(2, set.Questions.Count);
        Assert.Equal(3, set.ExpectedCount);
        Assert.Equal(["piv_90002", "piv_90003"], set.Questions[1].Expected);
        Assert.Equal([("q02", "piv_90003")], set.UnknownRulings(new HashSet<string> { "piv_90001", "piv_90002" }));
    }

    [Fact]
    public void HashIgnoresFormattingButNotAnswers()
    {
        var set = EvalQuestionSet.Parse(Valid, "test");
        var reformatted = EvalQuestionSet.Parse(JsonSerializer.Serialize(JsonDocument.Parse(Valid)), "test");
        var edited = EvalQuestionSet.Parse(Valid.Replace("\"piv_90003\"", "\"piv_90004\"", StringComparison.Ordinal), "test");

        Assert.Equal(set.Hash, reformatted.Hash);
        Assert.NotEqual(set.Hash, edited.Hash);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{"questions": []}""")]
    [InlineData("""{"questions": [{"question": "Sem id?", "expected": ["a"]}]}""")]
    [InlineData("""{"questions": [{"id": "q1", "question": " ", "expected": ["a"]}]}""")]
    [InlineData("""{"questions": [{"id": "q1", "question": "Sem respostas?", "expected": []}]}""")]
    [InlineData("""{"questions": [{"id": "q1", "question": "Resposta vazia?", "expected": [""]}]}""")]
    [InlineData("""{"questions": [{"id": "q1", "question": "Repetida?", "expected": ["a", "a"]}]}""")]
    [InlineData("""{"questions": [{"id": "q1", "question": "Um?", "expected": ["a"]}, {"id": "q1", "question": "Dois?", "expected": ["b"]}]}""")]
    [InlineData("""{"questions": [{"id": "q1", "question": "Igual?", "expected": ["a"]}, {"id": "q2", "question": "Igual?", "expected": ["b"]}]}""")]
    [InlineData("""{"questions": [{"id": "q1", "question": "Número?", "expected": [42]}]}""")]
    public void RejectsInvalidFiles(string json)
    {
        Assert.Throws<InvalidDataException>(() => EvalQuestionSet.Parse(json, "test"));
    }

    [Fact]
    public void RejectsOverlongQuestions()
    {
        var json = JsonSerializer.Serialize(new { questions = new[] { new { id = "q1", question = new string('a', 501), expected = new[] { "a" } } } });
        Assert.Throws<InvalidDataException>(() => EvalQuestionSet.Parse(json, "test"));
    }

    [Fact]
    public void HistoryReplacesARepeatedConfigurationKeepsItsTimeWhenScoresAreUnchangedAppendsANewOneAndRefusesAnEditedSet()
    {
        var set = EvalQuestionSet.Parse(Valid, "test");
        var history = new EvalHistory();
        var now = DateTimeOffset.UnixEpoch;
        ModeResult[] results = [new(SearchMode.Hybrid, set.Questions.Select(q => new QuestionOutcome(q, ["piv_90001"], 1, 1, 1, 1)).ToList())];

        ModeResult[] worse = [new(SearchMode.Hybrid, set.Questions.Select(q => new QuestionOutcome(q, ["piv_90009"], null, 0, 0, 0)).ToList())];

        history.Record(set, "eval/q.json", "config A", results, null, "initial", now);
        history.Record(set, "eval/q.json", "config A", results, null, null, now.AddHours(1));
        var unchangedAt = history.Iterations[0].RecordedAt;
        history.Record(set, "eval/q.json", "config A", worse, null, null, now.AddHours(2));
        var rescoredAt = history.Iterations[0].RecordedAt;
        history.Record(set, "eval/q.json", "config A", results, null, null, now.AddHours(3));
        var second = history.Record(set, "eval/q.json", "config B", results, null, "rrf k 30", now.AddHours(4));

        Assert.Equal(["baseline", "iteration 1"], history.Iterations.Select(i => i.Label));
        Assert.Equal("initial", history.Iterations[0].Note);
        Assert.Equal(now, unchangedAt);
        Assert.Equal(now.AddHours(2), rescoredAt);
        Assert.Equal(now.AddHours(3), history.Iterations[0].RecordedAt);
        Assert.Equal(1, history.Iterations[0].Modes["hybrid"].RecallAt10);
        Assert.Equal("config B", history.LatestConfig);
        Assert.Equal(1, second.Modes["hybrid"].RecallAt10);

        var edited = EvalQuestionSet.Parse(Valid.Replace("propinas", "propinas escolares", StringComparison.Ordinal), "test");
        var error = Assert.Throws<InvalidDataException>(() => history.Record(edited, "eval/q.json", "config C", results, null, null, now));
        Assert.Contains("frozen", error.Message, StringComparison.Ordinal);
        Assert.Equal(2, history.Iterations.Count);
    }

    [Fact]
    public void TheCommittedQuestionSetHasFiftyDocumentedQuestionsAndMatchesItsFrozenHash()
    {
        var root = RepositoryRoot();
        var set = EvalQuestionSet.Load(Path.Combine(root, "eval", "questions.json"));

        Assert.Equal(50, set.Questions.Count);
        Assert.Equal(Enumerable.Range(1, 50).Select(i => $"q{i:00}"), set.Questions.Select(q => q.Id));
        Assert.All(set.Questions, q =>
        {
            Assert.NotEmpty(q.Expected);
            Assert.NotEmpty(q.Article);
            Assert.All(q.Expected, id => Assert.Matches("^[a-z0-9_-]+$", id));
            Assert.DoesNotContain('—', q.Question);
        });
        Assert.True(set.Questions.Select(q => q.Article).Distinct().Count() >= 20, "questions should cover at least 20 articles");

        // Once tuning has started, the recorded history pins the questions and their answers.
        var historyPath = Path.Combine(root, "docs", "eval", "history.json");
        if (File.Exists(historyPath))
        {
            Assert.Equal(EvalHistory.Load(historyPath).QuestionsHash, set.Hash);
        }
    }

    [Fact]
    public void TheTaxIsAnOptionalLabelOutsideTheHash()
    {
        var plain = EvalQuestionSet.Parse(Valid, "test");
        var civa = EvalQuestionSet.Parse(Valid.Replace("\"article\": \"78-D\"", "\"tax\": \" CIVA \", \"article\": \"78-D\"", StringComparison.Ordinal), "test");

        Assert.Equal("", plain.Questions[0].Tax);
        Assert.Equal("CIVA", civa.Questions[0].Tax);
        Assert.Equal("", civa.Questions[1].Tax);
        // Search runs without filters, so the tax cannot change a score; the committed multi-tax set
        // pins it to the prefix of its expected ruling ids instead (see the test below).
        Assert.Equal(plain.Hash, civa.Hash);
    }

    [Fact]
    public void TheCommittedMultiTaxSetCoversAtLeastFourOtherTaxesAndMatchesItsFrozenHash()
    {
        var root = RepositoryRoot();
        var set = EvalQuestionSet.Load(Path.Combine(root, "eval", "questions-taxes.json"));

        Assert.True(set.Questions.Count >= 20, "the multi-tax set needs at least 20 questions");
        Assert.Equal(Enumerable.Range(1, set.Questions.Count).Select(i => $"t{i:00}"), set.Questions.Select(q => q.Id));
        Assert.All(set.Questions, q =>
        {
            Assert.NotEmpty(q.Expected);
            Assert.NotEmpty(q.Article);
            Assert.NotEmpty(q.Tax);
            Assert.NotEqual("CIRS", q.Tax);
            // Ids of the rulings each question's tax lists first (the index's display ids), never a CIRS ruling.
            Assert.All(q.Expected, id => Assert.Matches($"^{q.Tax.ToLowerInvariant()}-[a-z0-9_-]+$", id));
            Assert.DoesNotContain('—', q.Question);
        });
        Assert.True(set.Questions.Select(q => q.Tax).Distinct().Count() >= 4, "questions should cover at least 4 taxes other than CIRS");

        // Frozen before the first measurement: the history beside the report pins the questions and answers.
        var history = EvalHistory.Load(Path.Combine(root, "docs", "eval", "taxes", "history.json"));
        Assert.Equal(history.QuestionsHash, set.Hash);
        Assert.Equal("eval/questions-taxes.json", history.QuestionsPath);
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "LupaFiscal.slnx"))) return directory.FullName;
        }
        throw new InvalidOperationException("Repository root not found.");
    }
}
