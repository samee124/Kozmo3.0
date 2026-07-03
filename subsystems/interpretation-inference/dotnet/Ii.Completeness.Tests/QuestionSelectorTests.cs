using Ii.Completeness;
using Kozmo.Contracts;
using Xunit;

namespace Ii.Completeness.Tests;

public sealed class QuestionSelectorTests
{
    // ── L1 depth selects only L1 questions ──────────────────────────────────

    [Fact]
    public void Select_L1_returns_only_L1_questions()
    {
        var questions = QuestionSelector.Select(SaasQuestionBank.Category, DepthLevel.L1);

        Assert.All(questions, q => Assert.Equal(DepthLevel.L1, q.DepthLevel));
    }

    [Fact]
    public void Select_L1_returns_two_per_dimension_for_saas()
    {
        var questions = QuestionSelector.Select(SaasQuestionBank.Category, DepthLevel.L1);

        // 4 dimensions × 2 L1 questions each = 8
        Assert.Equal(8, questions.Count);
        foreach (var dim in Enum.GetValues<Dimension>())
            Assert.Equal(2, questions.Count(q => q.Dimension == dim));
    }

    // ── L2 depth includes L1 + L2 (the ladder) ──────────────────────────────

    [Fact]
    public void Select_L2_includes_L1_and_L2_questions()
    {
        var questions = QuestionSelector.Select(SaasQuestionBank.Category, DepthLevel.L2);

        Assert.All(questions, q => Assert.True(q.DepthLevel <= DepthLevel.L2));
        Assert.Equal(16, questions.Count); // 4 dims × 4 questions (L1+L2)
    }

    // ── L3 depth selects all questions ──────────────────────────────────────

    [Fact]
    public void Select_L3_returns_all_questions()
    {
        var questions = QuestionSelector.Select(SaasQuestionBank.Category, DepthLevel.L3);

        Assert.Equal(SaasQuestionBank.All.Count, questions.Count);
        Assert.Equal(24, questions.Count); // 4 dims × 6 questions (L1+L2+L3)
    }

    // ── Determinism ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData(DepthLevel.L1)]
    [InlineData(DepthLevel.L2)]
    [InlineData(DepthLevel.L3)]
    public void Select_same_inputs_produce_identical_ordered_set(DepthLevel depth)
    {
        var first  = QuestionSelector.Select(SaasQuestionBank.Category, depth);
        var second = QuestionSelector.Select(SaasQuestionBank.Category, depth);

        Assert.Equal(first.Count, second.Count);
        for (var i = 0; i < first.Count; i++)
            Assert.Equal(first[i].Id, second[i].Id);
    }

    [Fact]
    public void Select_order_is_stable_dimension_then_depth_then_id()
    {
        var questions = QuestionSelector.Select(SaasQuestionBank.Category, DepthLevel.L3);

        for (var i = 1; i < questions.Count; i++)
        {
            var prev = questions[i - 1];
            var curr = questions[i];

            var dimCmp = prev.Dimension.CompareTo(curr.Dimension);
            if (dimCmp < 0) continue; // dimension advanced — fine
            Assert.Equal(0, dimCmp);  // same dimension — depth or id must be >=

            var depthCmp = prev.DepthLevel.CompareTo(curr.DepthLevel);
            if (depthCmp < 0) continue; // depth advanced — fine
            Assert.Equal(0, depthCmp); // same depth — id must be >=

            Assert.True(
                string.Compare(prev.Id, curr.Id, StringComparison.Ordinal) <= 0,
                $"Order violation: '{prev.Id}' should precede '{curr.Id}'");
        }
    }

    // ── Unknown category returns empty ───────────────────────────────────────

    [Fact]
    public void Select_unknown_category_returns_empty()
    {
        var questions = QuestionSelector.Select("enterprise-hardware", DepthLevel.L3);
        Assert.Empty(questions);
    }

    // ── All questions have distinct IDs ─────────────────────────────────────

    [Fact]
    public void SaasBank_all_ids_are_unique()
    {
        var ids = SaasQuestionBank.All.Select(q => q.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct(StringComparer.Ordinal).Count());
    }

    // ── Required confidence is a valid probability ───────────────────────────

    [Fact]
    public void SaasBank_required_confidence_is_in_valid_range()
    {
        Assert.All(SaasQuestionBank.All, q =>
        {
            Assert.True(q.RequiredConfidence >= 0.0 && q.RequiredConfidence <= 1.0,
                $"Question '{q.Id}' has required_confidence {q.RequiredConfidence} outside [0,1].");
        });
    }
}
