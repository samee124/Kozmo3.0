using Ii.Contracts;
using Ii.Decay;
using Ii.Index;
using Kozmo.Contracts;
using Kozmo.Contracts.Config;
using Xunit;

namespace Ii.Tests;

/// <summary>
/// Step 0.3 — GATE: Determinism spike.
/// Same evidence in → byte-identical fingerprint out, every run.
/// Incremental recompute must equal full recompute.
/// </summary>
public sealed class DeterminismSpikeTests
{
    private readonly SaasProfile _profile = TestHelpers.LoadProfile();
    private readonly IndexModule  _idx     = new();
    private readonly DecayEngine  _decay   = new();

    [Fact]
    public void SameEvidence_ProducesIdenticalFingerprint_TwoRuns()
    {
        var entityId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
        var now      = new DateTimeOffset(2026, 6, 12, 10, 0, 0, TimeSpan.Zero);

        var beliefs  = BuildBeliefs(entityId, now);
        var scores   = ComputeAllScores(entityId, beliefs, now);

        var fp1 = _idx.ComputeFingerprint(scores, beliefs, _profile);
        var fp2 = _idx.ComputeFingerprint(scores, beliefs, _profile);

        Assert.Equal(fp1, fp2);
        Assert.NotEmpty(fp1);
        Assert.Equal(64, fp1.Length);  // SHA-256 hex
    }

    [Fact]
    public void Aggregate_ProducesIdenticalIndex_TwoRuns()
    {
        var entityId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000002");
        var now      = new DateTimeOffset(2026, 6, 12, 10, 0, 0, TimeSpan.Zero);

        var beliefs = BuildBeliefs(entityId, now);
        var scores  = ComputeAllScores(entityId, beliefs, now);

        var idx1 = _idx.Aggregate(entityId, scores, beliefs, null, _profile, now);
        var idx2 = _idx.Aggregate(entityId, scores, beliefs, null, _profile, now);

        Assert.Equal(idx1.Fingerprint, idx2.Fingerprint);
        Assert.Equal(idx1.Composite,   idx2.Composite);
        Assert.Equal(idx1.Band,        idx2.Band);
    }

    [Fact]
    public void IncrementalRecompute_EqualsFullRecompute()
    {
        var entityId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000003");
        var now      = new DateTimeOffset(2026, 6, 12, 10, 0, 0, TimeSpan.Zero);

        // Build initial state with 3 dimensions
        var beliefs3 = BuildBeliefs(entityId, now, skipDimension: Dimension.Strategic);
        var scores3  = ComputeAllScores(entityId, beliefs3, now);
        var initial  = _idx.Aggregate(entityId, scores3, beliefs3, null, _profile, now);

        // Add a new Strategic belief
        var newBelief = MakeBelief(entityId, Dimension.Strategic, "renewal_intent", 0.50,
                                   SourceTier.Reported, now);
        var allBeliefs = beliefs3.Append(newBelief).ToList();
        var allScores  = ComputeAllScores(entityId, allBeliefs, now);

        // Full recompute
        var full = _idx.Aggregate(entityId, allScores, allBeliefs, initial, _profile, now);

        // Incremental recompute (only Strategic changed)
        var stratScore  = allScores[Dimension.Strategic];
        var incremental = _idx.RecomputeDirty(entityId, Dimension.Strategic, stratScore,
                                              allBeliefs, initial, _profile, now);

        Assert.Equal(full.Fingerprint,    incremental.Fingerprint);
        Assert.Equal(full.Composite,      incremental.Composite,   5);
        Assert.Equal(full.Band,           incremental.Band);
        Assert.Equal(full.ConfidenceFloor,incremental.ConfidenceFloor, 5);
    }

    [Fact]
    public void DifferentEvidence_ProducesDifferentFingerprint()
    {
        var entityId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000004");
        var now      = new DateTimeOffset(2026, 6, 12, 10, 0, 0, TimeSpan.Zero);

        var beliefs1 = BuildBeliefs(entityId, now);
        var scores1  = ComputeAllScores(entityId, beliefs1, now);
        var fp1      = _idx.ComputeFingerprint(scores1, beliefs1, _profile);

        // Swap one belief value
        var tweaked  = beliefs1.Select((b, i) => i == 0 ? b with { Value = b.Value + 0.1 } : b).ToList();
        var scores2  = ComputeAllScores(entityId, tweaked, now);
        var fp2      = _idx.ComputeFingerprint(scores2, tweaked, _profile);

        Assert.NotEqual(fp1, fp2);
    }

    [Fact]
    public void ConfidenceFloor_PreventsAtRiskFromBecomingCritical_WhenReportedSignalOnly()
    {
        var entityId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000005");
        var now      = new DateTimeOffset(2026, 6, 12, 10, 0, 0, TimeSpan.Zero);

        // All four dimensions with Reported (0.50) tier → confidence_floor = 0.50 < 0.60
        var beliefs = new[]
        {
            MakeBelief(entityId, Dimension.Operational,  "uptime_sla",        0.10, SourceTier.Reported, now),
            MakeBelief(entityId, Dimension.Experiential, "csat_score",         0.10, SourceTier.Reported, now),
            MakeBelief(entityId, Dimension.Financial,    "payment_timeliness", 0.10, SourceTier.Reported, now),
            MakeBelief(entityId, Dimension.Strategic,    "renewal_intent",     0.10, SourceTier.Reported, now),
        };
        var scores = ComputeAllScores(entityId, beliefs, now);
        var idx    = _idx.Aggregate(entityId, scores, beliefs, null, _profile, now);

        // Composite ≈ 0.10 → would be Critical, but confidence_floor = 0.50 < 0.60 → must be AtRisk
        Assert.Equal(Band.AtRisk,   idx.Band);
        Assert.True(idx.ConfidenceFloor < 0.60);
        Assert.True(idx.Composite < _profile.Bands.AtRiskMin);
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private IReadOnlyList<Belief> BuildBeliefs(Guid entityId, DateTimeOffset now,
        Dimension? skipDimension = null)
    {
        var all = new[]
        {
            MakeBelief(entityId, Dimension.Operational,  "uptime_sla",        0.45, SourceTier.Verified, now),
            MakeBelief(entityId, Dimension.Experiential, "adoption_rate",      0.40, SourceTier.Inferred, now),
            MakeBelief(entityId, Dimension.Financial,    "payment_timeliness", 0.55, SourceTier.Verified, now),
            MakeBelief(entityId, Dimension.Strategic,    "renewal_intent",     0.50, SourceTier.Reported, now),
        };
        return skipDimension.HasValue
            ? all.Where(b => b.Dimension != skipDimension.Value).ToList()
            : all;
    }

    private IReadOnlyDictionary<Dimension, DimensionScore> ComputeAllScores(
        Guid entityId, IReadOnlyList<Belief> beliefs, DateTimeOffset now)
    {
        var decayed = beliefs.Select(b => _decay.WithCurrentFreshness(b, _profile, now)).ToList();
        var byDim   = decayed.Where(b => b.Dimension.HasValue).GroupBy(b => b.Dimension!.Value).ToDictionary(g => g.Key, g => g.ToList());

        var allDims = new[] { Dimension.Operational, Dimension.Experiential, Dimension.Financial, Dimension.Strategic };
        return allDims.ToDictionary(d => d, d =>
        {
            var dimBeliefs = byDim.TryGetValue(d, out var bl) ? bl : new List<Belief>();
            if (dimBeliefs.Count == 0)
                return new DimensionScore(entityId, d, 0.5, 0.0, []);
            var totalW = dimBeliefs.Sum(b => b.Confidence);
            var score  = totalW > 0 ? dimBeliefs.Sum(b => b.Value * b.Confidence) / totalW : dimBeliefs.Average(b => b.Value);
            return new DimensionScore(entityId, d, score, dimBeliefs.Max(b => b.Confidence), dimBeliefs.Select(b => b.Id).ToList());
        });
    }

    private Belief MakeBelief(Guid entityId, Dimension dim, string criterion, double value,
        SourceTier tier, DateTimeOffset now)
    {
        var tierWeight = _profile.SourceTiers.TryGetValue(tier.ToString(), out var tc) ? tc.Weight : 0.5;
        return new Belief(
            Id:            Guid.NewGuid(),
            EntityId:      entityId,
            Dimension:     dim,
            Criterion:     criterion,
            Value:         value,
            SourceTier:    tier,
            Confidence:    tierWeight,
            Freshness:     1.0,
            Derivation:    $"test:{criterion}",
            SourceSignals: [],
            Version:       1,
            SupersededBy:  null,
            CreatedAt:     now,
            TraceId:       Guid.NewGuid());
    }
}
