using Kozmo.Contracts;
using Kozmo.Contracts.Config;
using Xunit;

namespace Ii.Completeness.Tests;

public sealed class QuestionBankValidatorTests
{
    private static readonly SaasProfile EmptyProfile = new(
        ConfigVersion:       "test",
        Dimensions:          new Dictionary<string, DimensionDefinition>(),
        ScoringRubric:       new Dictionary<string, CriterionRubric>(),
        DimensionWeights:    new Dictionary<string, double>(),
        Bands:               new BandsConfig(0.6, 0.4, 0.5, 0.1, 0.05),
        PostureRules:        new List<PostureRule>(),
        SourceTiers:         new Dictionary<string, SourceTierConfig>(),
        ClassificationRules: new List<ClassificationRule>(),
        HalfLifeDays:        new Dictionary<string, int>(),
        EntityResolution:    new EntityResolutionConfig("exact", 0.85, new Dictionary<string, string>()));

    // Mirrors the real catalogue's sla_uptime/csat entries closely enough to prove the real
    // bindings resolve — not a fabricated fixture, the same two keys the real E2 bridge binds.
    private static readonly SaasProfile RealisticProfile = EmptyProfile with
    {
        ClaimKeyCatalogue = new Dictionary<string, ClaimKeyDefinition>
        {
            ["sla_uptime"] = new("scored", "percent", "Operational", "Verified", 30, 0.25) { RubricCriterion = "uptime_sla" },
            ["csat"]       = new("scored", "rating",  "Experiential", "Reported", 60, 0.25) { RubricCriterion = "csat_score" },
        }
    };

    [Fact]
    public void ValidateBindings_RealSaasQuestionBank_AgainstRealBoundKeys_DoesNotThrow()
    {
        // Confirms the CRITICAL constraint: today's real bindings (sla_uptime, csat) resolve
        // cleanly. Uses the real SaasQuestionBank.All, not a fixture.
        QuestionBankValidator.ValidateBindings(RealisticProfile);
    }

    [Fact]
    public void ValidateBindings_QuestionBoundToUnknownClaimKey_Throws()
    {
        var fakeQuestions = new List<Question>
        {
            new("fake.q.1", Dimension.Operational, "A fake question for this test only.",
                AnswerType.TypedValue, DepthLevel.L1, 0.60, TargetClaimKey: "totally_nonexistent_claim_key"),
        };

        var ex = Assert.Throws<InvalidOperationException>(
            () => QuestionBankValidator.ValidateBindings(fakeQuestions, RealisticProfile));
        Assert.Contains("fake.q.1", ex.Message);
        Assert.Contains("totally_nonexistent_claim_key", ex.Message);
    }

    [Fact]
    public void ValidateBindings_UnboundQuestion_NeverChecked_DoesNotThrow()
    {
        var fakeQuestions = new List<Question>
        {
            new("fake.q.2", Dimension.Operational, "An unbound fake question.",
                AnswerType.YesNo, DepthLevel.L1, 0.60), // TargetClaimKey defaults to null
        };

        QuestionBankValidator.ValidateBindings(fakeQuestions, RealisticProfile);
    }
}
