using Ii.Contracts;
using Ii.Index;
using Kozmo.Contracts;
using Xunit;

namespace Ii.Tests;

/// <summary>
/// Class M — Annotation fields are excluded from the fingerprint (B2-verify Check 3).
///
/// M1  Two beliefs with identical (Dimension, Criterion, Value, Confidence)
///     but different ClassificationMethod / ClassificationConfidence / ReasoningSummary
///     → produce the SAME fingerprint.
/// </summary>
public sealed class FingerprintAnnotationTests
{
    [Fact]
    [Trait("Class", "M")]
    public void M1_AnnotationFields_DoNotAffect_Fingerprint()
    {
        var profile  = TestHelpers.LoadProfile();
        var index    = new IndexModule();
        var now      = new DateTimeOffset(2026, 6, 15, 10, 0, 0, TimeSpan.Zero);
        var entityId = Guid.NewGuid();

        // Base belief: rule-classified (all annotation fields at defaults)
        var ruleBelief = new Belief(
            Id:            Guid.NewGuid(),
            EntityId:      entityId,
            Dimension:     Dimension.Experiential,
            Criterion:     "adoption_rate",
            Value:         0.40,
            SourceTier:    SourceTier.Verified,
            Confidence:    0.85,
            Freshness:     0.90,
            Derivation:    "ua:adoption_pct",
            SourceSignals: [],
            Version:       1,
            SupersededBy:  null,
            CreatedAt:     now,
            TraceId:       Guid.NewGuid());

        // LLM variant: identical core tuple (Dimension, Criterion, Value, Confidence),
        // but annotation fields all differ.
        var llmBelief = ruleBelief with
        {
            ClassificationMethod     = ClassificationMethod.Llm,
            ClassificationConfidence = 0.85,
            ReasoningSummary         = "CSM note indicates 30-35% adoption rate of licensed seats."
        };

        // Same dimension scores for both aggregations
        var scores = new Dictionary<Dimension, DimensionScore>
        {
            [Dimension.Experiential] = new DimensionScore(entityId, Dimension.Experiential, 0.40, 0.85, [ruleBelief.Id])
        };

        var idx1 = index.Aggregate(entityId, scores, [ruleBelief], null, profile, now);
        var idx2 = index.Aggregate(entityId, scores, [llmBelief],  null, profile, now);

        // Fingerprints must be identical — annotation fields are NOT fingerprint inputs
        Assert.Equal(64, idx1.Fingerprint.Length);
        Assert.Equal(idx1.Fingerprint, idx2.Fingerprint);
    }

    [Fact]
    [Trait("Class", "M")]
    public void M2_CoreTupleDifference_Changes_Fingerprint()
    {
        // Control: changing Value DOES change the fingerprint (sanity check)
        var profile  = TestHelpers.LoadProfile();
        var index    = new IndexModule();
        var now      = new DateTimeOffset(2026, 6, 15, 10, 0, 0, TimeSpan.Zero);
        var entityId = Guid.NewGuid();

        var belief1 = new Belief(
            Id:            Guid.NewGuid(),
            EntityId:      entityId,
            Dimension:     Dimension.Experiential,
            Criterion:     "adoption_rate",
            Value:         0.40,
            SourceTier:    SourceTier.Verified,
            Confidence:    0.85,
            Freshness:     0.90,
            Derivation:    "ua:adoption_pct",
            SourceSignals: [],
            Version:       1,
            SupersededBy:  null,
            CreatedAt:     now,
            TraceId:       Guid.NewGuid());

        var belief2 = belief1 with { Value = 0.30 };  // different Value

        var scores1 = new Dictionary<Dimension, DimensionScore>
        {
            [Dimension.Experiential] = new DimensionScore(entityId, Dimension.Experiential, 0.40, 0.85, [belief1.Id])
        };
        var scores2 = new Dictionary<Dimension, DimensionScore>
        {
            [Dimension.Experiential] = new DimensionScore(entityId, Dimension.Experiential, 0.30, 0.85, [belief2.Id])
        };

        var idx1 = index.Aggregate(entityId, scores1, [belief1], null, profile, now);
        var idx2 = index.Aggregate(entityId, scores2, [belief2], null, profile, now);

        // Different core tuple → different fingerprint
        Assert.NotEqual(idx1.Fingerprint, idx2.Fingerprint);
    }
}
