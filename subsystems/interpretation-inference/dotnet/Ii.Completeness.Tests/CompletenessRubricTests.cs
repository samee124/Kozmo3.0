using Ii.Completeness;
using Kozmo.Contracts;
using Xunit;

namespace Ii.Completeness.Tests;

public sealed class CompletenessRubricTests
{
    private static IReadOnlyList<Question> L1Questions =>
        QuestionSelector.Select(SaasQuestionBank.Category, DepthLevel.L1);

    private static IReadOnlyList<Question> L2Questions =>
        QuestionSelector.Select(SaasQuestionBank.Category, DepthLevel.L2);

    private static IReadOnlyList<Question> AllQuestions =>
        QuestionSelector.Select(SaasQuestionBank.Category, DepthLevel.L3);

    // ── Rich vendor: all questions answered above bar ───────────────────────

    [Fact]
    public void Rich_vendor_all_answered_at_L1_produces_full_coverage()
    {
        var questions = L1Questions;
        var answers   = questions.Select(q => new Answer(q.Id, "answered", q.RequiredConfidence)).ToList();

        var profile = CompletenessRubric.Compute(questions, answers);

        Assert.Equal(questions.Count, profile.AnsweredQuestionIds.Count);
        Assert.Empty(profile.GapQuestionIds);
        Assert.Equal(1.0, profile.OverallCoverage, precision: 10);

        foreach (var cov in profile.DimensionCoverages)
        {
            if (cov.RequiredCount > 0)
                Assert.Equal(1.0, cov.Coverage, precision: 10);
        }
    }

    [Fact]
    public void Rich_vendor_all_answered_at_L3_produces_full_coverage()
    {
        var questions = AllQuestions;
        var answers   = questions.Select(q => new Answer(q.Id, "answered", 0.90)).ToList();

        var profile = CompletenessRubric.Compute(questions, answers);

        Assert.Equal(24, profile.AnsweredQuestionIds.Count);
        Assert.Empty(profile.GapQuestionIds);
        Assert.Equal(1.0, profile.OverallCoverage, precision: 10);
    }

    // ── Sparse vendor: only Financial answered — exact gap set verified ─────

    [Fact]
    public void Sparse_vendor_only_financial_answered_gaps_are_exactly_non_financial_ids()
    {
        var questions = L1Questions; // 8 questions: saas.{op,exp,fin,str}.l1.{1,2}

        var answers = questions
            .Where(q => q.Dimension == Dimension.Financial)
            .Select(q => new Answer(q.Id, "answered", 0.90))
            .ToList();

        var profile = CompletenessRubric.Compute(questions, answers);

        // Answered set must be exactly the two Financial L1 question ids.
        var expectedAnswered = new[] { "saas.fin.l1.1", "saas.fin.l1.2" }
            .OrderBy(id => id, StringComparer.Ordinal).ToList();
        var actualAnswered   = profile.AnsweredQuestionIds
            .OrderBy(id => id, StringComparer.Ordinal).ToList();
        Assert.Equal(expectedAnswered, actualAnswered);

        // Gap set must be exactly the six non-Financial L1 question ids — not just "6 gaps"
        // or "non-Financial dimensions have 0 coverage", but these specific ids and no others.
        var expectedGaps = new[]
        {
            "saas.exp.l1.1", "saas.exp.l1.2",
            "saas.op.l1.1",  "saas.op.l1.2",
            "saas.str.l1.1", "saas.str.l1.2",
        }.OrderBy(id => id, StringComparer.Ordinal).ToList();
        var actualGaps = profile.GapQuestionIds
            .OrderBy(id => id, StringComparer.Ordinal).ToList();
        Assert.Equal(expectedGaps, actualGaps);

        // Financial dimension must show full coverage (2/2); all others 0/2.
        var financial = profile.DimensionCoverages.Single(d => d.Dimension == Dimension.Financial);
        Assert.Equal(2, financial.AnsweredCount);
        Assert.Equal(2, financial.RequiredCount);

        foreach (var dim in new[] { Dimension.Operational, Dimension.Experiential, Dimension.Strategic })
        {
            var cov = profile.DimensionCoverages.Single(d => d.Dimension == dim);
            Assert.Equal(0, cov.AnsweredCount);
            Assert.Equal(2, cov.RequiredCount);
        }
    }

    // ── Below-bar answers are treated as gaps ────────────────────────────────

    [Fact]
    public void Answers_below_required_confidence_are_gaps()
    {
        var questions = L1Questions;
        // All answers are 0.01 below their required_confidence bar
        var answers = questions
            .Select(q => new Answer(q.Id, "answered", q.RequiredConfidence - 0.01))
            .ToList();

        var profile = CompletenessRubric.Compute(questions, answers);

        Assert.Empty(profile.AnsweredQuestionIds);
        Assert.Equal(questions.Count, profile.GapQuestionIds.Count);
        Assert.Equal(0.0, profile.OverallCoverage, precision: 10);
    }

    [Fact]
    public void Answer_exactly_at_required_confidence_counts_as_answered()
    {
        var q       = AllQuestions.First();
        var answer  = new Answer(q.Id, "yes", q.RequiredConfidence);

        var profile = CompletenessRubric.Compute([q], [answer]);

        Assert.Single(profile.AnsweredQuestionIds);
        Assert.Empty(profile.GapQuestionIds);
    }

    // ── No answers → all gaps ────────────────────────────────────────────────

    [Fact]
    public void No_answers_produces_all_gaps()
    {
        var questions = L1Questions;

        var profile = CompletenessRubric.Compute(questions, []);

        Assert.Empty(profile.AnsweredQuestionIds);
        Assert.Equal(questions.Count, profile.GapQuestionIds.Count);
        Assert.Equal(0.0, profile.OverallCoverage, precision: 10);
    }

    // ── Gaps contain the RIGHT questions ────────────────────────────────────

    [Fact]
    public void Gaps_contain_exactly_the_unanswered_question_ids()
    {
        var questions = L1Questions;
        var answered  = questions.Where(q => q.Dimension == Dimension.Operational).ToList();
        var expected  = questions.Where(q => q.Dimension != Dimension.Operational)
                                 .OrderBy(q => q.Id, StringComparer.Ordinal)
                                 .Select(q => q.Id)
                                 .ToList();

        var answers = answered.Select(q => new Answer(q.Id, "yes", 0.90)).ToList();
        var profile = CompletenessRubric.Compute(questions, answers);

        var actualGaps = profile.GapQuestionIds.OrderBy(id => id, StringComparer.Ordinal).ToList();
        Assert.Equal(expected, actualGaps);
    }

    // ── Determinism ──────────────────────────────────────────────────────────

    [Fact]
    public void Same_inputs_produce_identical_profile()
    {
        var questions = L2Questions;
        var answers   = questions.Take(5).Select(q => new Answer(q.Id, "yes", 0.80)).ToList();

        var first  = CompletenessRubric.Compute(questions, answers);
        var second = CompletenessRubric.Compute(questions, answers);

        Assert.Equal(first.AnsweredQuestionIds, second.AnsweredQuestionIds);
        Assert.Equal(first.GapQuestionIds,      second.GapQuestionIds);
        Assert.Equal(first.OverallCoverage,     second.OverallCoverage);

        for (var i = 0; i < first.DimensionCoverages.Count; i++)
        {
            Assert.Equal(first.DimensionCoverages[i].Dimension,    second.DimensionCoverages[i].Dimension);
            Assert.Equal(first.DimensionCoverages[i].AnsweredCount, second.DimensionCoverages[i].AnsweredCount);
            Assert.Equal(first.DimensionCoverages[i].RequiredCount, second.DimensionCoverages[i].RequiredCount);
        }
    }

    // ── All 4 dimensions represented in every profile ────────────────────────

    [Fact]
    public void Profile_always_contains_all_four_dimensions()
    {
        var profile = CompletenessRubric.Compute(AllQuestions, []);

        var dims = profile.DimensionCoverages.Select(d => d.Dimension).ToHashSet();
        foreach (var dim in Enum.GetValues<Dimension>())
            Assert.Contains(dim, dims);
    }

    // ── Two-sided check: rich has high coverage, sparse has honest gaps ───────

    [Fact]
    public void Two_sided_rich_vs_sparse_at_L2()
    {
        var questions = L2Questions; // 16 questions

        // Rich vendor: all answered at high confidence
        var richAnswers = questions.Select(q => new Answer(q.Id, "yes", 0.90)).ToList();
        var richProfile = CompletenessRubric.Compute(questions, richAnswers);

        // Sparse vendor: only Operational answered
        var sparseAnswers = questions
            .Where(q => q.Dimension == Dimension.Operational)
            .Select(q => new Answer(q.Id, "yes", 0.90))
            .ToList();
        var sparseProfile = CompletenessRubric.Compute(questions, sparseAnswers);

        Assert.Equal(1.0, richProfile.OverallCoverage, precision: 10);
        Assert.True(sparseProfile.OverallCoverage < 0.35,
            $"Sparse vendor coverage {sparseProfile.OverallCoverage} should be well below 0.35");

        // Sparse profile should show gaps in the three non-Operational dimensions
        var sparseCoverages = sparseProfile.DimensionCoverages
            .Where(d => d.Dimension != Dimension.Operational)
            .ToList();
        Assert.All(sparseCoverages, d => Assert.Equal(0.0, d.Coverage, precision: 10));
    }
}
