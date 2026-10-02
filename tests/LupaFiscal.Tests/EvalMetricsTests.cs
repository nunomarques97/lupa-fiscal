using LupaFiscal.Core.Evaluation;
using LupaFiscal.Core.Search;

namespace LupaFiscal.Tests;

public sealed class EvalMetricsTests
{
    [Fact]
    public void DuplicateChunksOfOneRulingCountOnceAtTheirFirstPosition()
    {
        // Passages in search order: A has three chunks, so naive chunk ranks would put C at 4 and D at 6.
        string[] ranked = ["A", "A", "B", "C", "A", "D"];

        Assert.Equal(["A", "B", "C", "D"], RetrievalMetrics.DistinctRulings(ranked));
        Assert.Equal(3, RetrievalMetrics.FirstRelevantRank(ranked, ["C"], 10));
        Assert.Equal(1.0 / 3, RetrievalMetrics.ReciprocalRank(ranked, ["C"], 10), 12);
        Assert.Equal(0, RetrievalMetrics.RecallAtK(ranked, ["C"], 2));
        Assert.Equal(1, RetrievalMetrics.RecallAtK(ranked, ["C"], 3));
        Assert.Equal(1, RetrievalMetrics.RecallAtK(ranked, ["D"], 4));
        Assert.Equal(0, RetrievalMetrics.ReciprocalRank(ranked, ["D"], 3));
    }

    [Fact]
    public void RecallNeedsOneExpectedRulingAndMrrTakesTheFirst()
    {
        string[] ranked = ["A", "B", "C", "D", "E"];

        // Two alternative answers at ranks 2 and 4: answered, reciprocal rank of the first (1/2).
        Assert.Equal(1, RetrievalMetrics.RecallAtK(ranked, ["D", "B"], 3));
        Assert.Equal(0.5, RetrievalMetrics.ReciprocalRank(ranked, ["D", "B"], 3));
        // Coverage: 1 of the 2 expected within the first 3; both within 5.
        Assert.Equal(0.5, RetrievalMetrics.CoverageAtK(ranked, ["D", "B"], 3));
        Assert.Equal(1, RetrievalMetrics.CoverageAtK(ranked, ["D", "B"], 5));
        // Not retrieved at all.
        Assert.Equal(0, RetrievalMetrics.RecallAtK(ranked, ["Z"], 5));
        Assert.Null(RetrievalMetrics.FirstRelevantRank(ranked, ["Z"], 5));
        // An empty result list.
        Assert.Equal(0, RetrievalMetrics.ReciprocalRank([], ["A"], 10));
    }

    [Fact]
    public void CoverageIsCappedAtKWhenMoreRulingsAreExpected()
    {
        var expected = Enumerable.Range(1, 12).Select(i => $"R{i}").ToList();
        var ranked = expected.Take(10).Concat(["X"]).ToList();

        Assert.Equal(1, RetrievalMetrics.CoverageAtK(ranked, expected, 10));
        Assert.Equal(0.5, RetrievalMetrics.CoverageAtK(["R1", "X", "R2", "Y"], expected, 4));
    }

    [Fact]
    public void TiedFusionScoresKeepTheSearchersOrder()
    {
        // Ruling 1 is first by keyword and second by vector, ruling 2 the reverse: equal RRF scores
        // (1/61 + 1/62). The tie-break (best single rank, then id) puts 1 before 2, and the metrics
        // use that order: 2 is at rank 2, so recall@1 is 0 and its reciprocal rank 1/2.
        var fused = ReciprocalRankFusion.Fuse([[1, 2], [2, 1]], 60);
        Assert.Equal(fused[0].Score, fused[1].Score, 12);
        var ranked = fused.Select(f => f.Id == 1 ? "A" : "B").ToList();

        Assert.Equal(["A", "B"], ranked);
        Assert.Equal(0, RetrievalMetrics.RecallAtK(ranked, ["B"], 1));
        Assert.Equal(0.5, RetrievalMetrics.ReciprocalRank(ranked, ["B"], 10));

        // A tie straddling the cut-off: the ruling placed after the k-th position is not counted.
        string[] boundary = ["A", "B", "C"]; // B and C tied; the searcher placed C third
        Assert.Equal(0, RetrievalMetrics.RecallAtK(boundary, ["C"], 2));
        Assert.Equal(0, RetrievalMetrics.CoverageAtK(boundary, ["C"], 2));
    }

    [Fact]
    public void ModeAveragesAreHandComputed()
    {
        var question = new EvalQuestion("q", "?", "", ["X"]);
        QuestionOutcome Outcome(params string[] ranked) => new(question, ranked,
            RetrievalMetrics.FirstRelevantRank(ranked, ["X"], 50),
            RetrievalMetrics.RecallAtK(ranked, ["X"], 10),
            RetrievalMetrics.ReciprocalRank(ranked, ["X"], 10),
            RetrievalMetrics.CoverageAtK(ranked, ["X"], 10));

        var result = new ModeResult(SearchMode.Hybrid,
        [
            Outcome("X", "A"),                                                   // rank 1
            Outcome("A", "A", "B", "X"),                                         // rank 3 (A counted once)
            Outcome("A", "B", "C", "D", "E", "F", "G", "H", "I", "J", "X"),      // rank 11: a miss at 10
            Outcome("A"),                                                        // absent
        ]);

        Assert.Equal(0.5, result.RecallAtK, 12);                  // 2 of 4
        Assert.Equal((1 + 1.0 / 3) / 4, result.Mrr, 12);         // (1 + 1/3 + 0 + 0) / 4
        Assert.Equal(2, result.Answered);
        Assert.Equal(11, result.Outcomes[2].FirstExpectedRank);
        Assert.Null(result.Outcomes[3].FirstExpectedRank);
    }

    [Fact]
    public void PercentilesUseTheNearestRank()
    {
        var twenty = Enumerable.Range(1, 20).Select(i => (double)i).Reverse().ToList();
        Assert.Equal(10, RetrievalMetrics.Percentile(twenty, 50));
        Assert.Equal(19, RetrievalMetrics.Percentile(twenty, 95));
        Assert.Equal(20, RetrievalMetrics.Percentile(twenty, 100));

        var fifty = Enumerable.Range(1, 50).Select(i => i * 10.0).ToList();
        Assert.Equal(250, RetrievalMetrics.Percentile(fifty, 50));
        Assert.Equal(480, RetrievalMetrics.Percentile(fifty, 95)); // ceil(47.5) = 48th value

        Assert.Equal(7, RetrievalMetrics.Percentile([7], 95));
        Assert.Throws<ArgumentOutOfRangeException>(() => RetrievalMetrics.Percentile(fifty, 0));
        Assert.Throws<ArgumentException>(() => RetrievalMetrics.Percentile([], 50));
    }
}
