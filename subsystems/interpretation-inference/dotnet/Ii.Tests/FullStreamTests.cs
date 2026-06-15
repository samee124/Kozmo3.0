using Ii.Contracts;
using Ii.Decay;
using Ii.Index;
using Ii.Posture;
using Ii.Rubric;
using Kozmo.Contracts;
using Kozmo.Contracts.Config;
using Xunit;

namespace Ii.Tests;

/// <summary>
/// Phase A1 — comprehensive test suite across Classes A–F.
/// All tests compile against frozen contracts; the failing set defines the A1 to-do list.
///
/// Class A  FullStreamGolden  — end-to-end band + stance per vendor
/// Class B  Structural        — confidence gate, renewal window, REPORTED tier invariant
/// Class C  Determinism       — fingerprint stability per vendor
/// Class D  IncrementalFull   — incremental recompute == full recompute per vendor
/// Class E  Decay             — half-life decay properties
/// Class F  RegressionPin     — frozen subset + fingerprint pin
/// </summary>
public sealed class FullStreamTests
{
    // ═══════════════════════════════════════════════════════════════════════════
    // CLASS A — FullStreamGolden
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    [Trait("Class", "A")]
    public void A1_Cloudwave_Full_Stream_IsAtRisk_Renegotiate()
    {
        using var h = TestHarness.FreshEngineWithSeed();
        h.ReplayAllSignals("cloudwave");

        var idx     = h.GetIndex("cloudwave");
        var posture = h.GetPosture("cloudwave");

        Assert.NotNull(idx);
        Assert.NotNull(posture);
        Assert.Equal(Band.AtRisk,        idx.Band);
        Assert.Equal(Stance.Renegotiate, posture.Stance);
    }

    [Fact]
    [Trait("Class", "A")]
    public void A2_Corvus_Full_Stream_IsCritical_Escalate()
    {
        using var h = TestHarness.FreshEngineWithSeed();
        h.ReplayAllSignals("corvus");

        var idx     = h.GetIndex("corvus");
        var posture = h.GetPosture("corvus");

        Assert.NotNull(idx);
        Assert.NotNull(posture);
        Assert.Equal(Band.Critical,  idx.Band);
        Assert.Equal(Stance.Escalate, posture.Stance);
    }

    [Fact]
    [Trait("Class", "A")]
    public void A3_Meridian_Full_Stream_IsHealthy_Maintain()
    {
        using var h = TestHarness.FreshEngineWithSeed();
        h.ReplayAllSignals("meridian");

        var idx     = h.GetIndex("meridian");
        var posture = h.GetPosture("meridian");

        Assert.NotNull(idx);
        Assert.NotNull(posture);
        Assert.Equal(Band.Healthy,   idx.Band);
        Assert.Equal(Stance.Maintain, posture.Stance);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // CLASS B — StructuralGuarantees
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    [Trait("Class", "B")]
    public void B1_Corvus_ConfidenceFloor_AboveGate_Enables_Critical()
    {
        using var h = TestHarness.FreshEngineWithSeed();
        h.ReplayAllSignals("corvus");

        var idx = h.GetIndex("corvus");

        Assert.NotNull(idx);
        Assert.Equal(Band.Critical, idx.Band);
        Assert.True(idx.ConfidenceFloor >= 0.60,
            $"ConfidenceFloor {idx.ConfidenceFloor:F4} must be >= 0.60 (all Corvus signals are Verified)");
    }

    [Fact]
    [Trait("Class", "B")]
    public void B2_Critical_RenewalWithin30Days_Drives_Remediate()
    {
        // PostureModule direct test: Critical + Stable + renewal ≤ 30 days → Remediate
        // (default Critical + Stable with no renewal constraint → Escalate)
        var profile = TestHelpers.LoadProfile();
        var posture = new PostureModule();
        var now     = new DateTimeOffset(2026, 6, 15, 10, 0, 0, TimeSpan.Zero);
        var renewal = now.AddDays(20);   // 20 ≤ 30-day window
        var id      = Guid.NewGuid();

        var scores = CriticalScores(id);
        // Stable pattern: previous and current have identical composite
        var prevIdx = new EntityIndex(id, scores, 0.26, 0.90, Band.Critical, "fp_prev", 1, now.AddDays(-7));
        var currIdx = new EntityIndex(id, scores, 0.26, 0.90, Band.Critical, "fp_curr", 2, now);

        var result = posture.Assign(currIdx, prevIdx, renewal, profile, now);

        Assert.Equal(Stance.Remediate, result.Stance);
    }

    [Fact]
    [Trait("Class", "B")]
    public void B2b_Critical_NoRenewalConstraint_Drives_Escalate()
    {
        // Control for B2: same Critical + Stable, but no renewal constraint → Escalate
        var profile = TestHelpers.LoadProfile();
        var posture = new PostureModule();
        var now     = new DateTimeOffset(2026, 6, 15, 10, 0, 0, TimeSpan.Zero);
        var id      = Guid.NewGuid();

        var scores  = CriticalScores(id);
        var prevIdx = new EntityIndex(id, scores, 0.26, 0.90, Band.Critical, "fp_prev", 1, now.AddDays(-7));
        var currIdx = new EntityIndex(id, scores, 0.26, 0.90, Band.Critical, "fp_curr", 2, now);

        var result = posture.Assign(currIdx, prevIdx, contractRenewalDate: null, profile, now);

        Assert.Equal(Stance.Escalate, result.Stance);
    }

    [Fact]
    [Trait("Class", "B")]
    public void B3_Reported_Tier_Cannot_Force_Critical()
    {
        // Structural invariant: confidence_floor for REPORTED (0.50) < critical gate (0.60)
        // → composite < AtRiskMin stays AtRisk, never Critical
        var profile  = TestHelpers.LoadProfile();
        var index    = new IndexModule();
        var now      = new DateTimeOffset(2026, 6, 15, 10, 0, 0, TimeSpan.Zero);
        var entityId = Guid.NewGuid();

        var beliefs = new[]
        {
            MakeBelief(entityId, Dimension.Operational,  "uptime_sla",        0.10, SourceTier.Reported, profile, now),
            MakeBelief(entityId, Dimension.Experiential, "csat_score",         0.10, SourceTier.Reported, profile, now),
            MakeBelief(entityId, Dimension.Financial,    "payment_timeliness", 0.10, SourceTier.Reported, profile, now),
            MakeBelief(entityId, Dimension.Strategic,    "renewal_intent",     0.10, SourceTier.Reported, profile, now),
        };
        var scores = AllDimScores(entityId, beliefs, profile);
        var idx    = index.Aggregate(entityId, scores, beliefs, null, profile, now);

        Assert.Equal(Band.AtRisk, idx.Band);
        Assert.True(idx.ConfidenceFloor < 0.60,
            $"Reported confidence_floor {idx.ConfidenceFloor:F4} must be < 0.60");
        Assert.True(idx.Composite < profile.Bands.AtRiskMin,
            $"Composite {idx.Composite:F4} should be below AtRiskMin so gate is the deciding factor");
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // CLASS C — Determinism (fingerprint stability)
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    [Trait("Class", "C")]
    public void C1_Cloudwave_Fingerprint_IsStable_AcrossTwoRuns()
    {
        string fp1, fp2;

        using (var h = TestHarness.FreshEngineWithSeed())
        {
            h.ReplayAllSignals("cloudwave");
            fp1 = h.GetIndex("cloudwave")!.Fingerprint;
        }
        using (var h = TestHarness.FreshEngineWithSeed())
        {
            h.ReplayAllSignals("cloudwave");
            fp2 = h.GetIndex("cloudwave")!.Fingerprint;
        }

        Assert.Equal(fp1, fp2);
        Assert.Equal(64, fp1.Length);
    }

    [Fact]
    [Trait("Class", "C")]
    public void C2_Corvus_Fingerprint_IsStable_AcrossTwoRuns()
    {
        string fp1, fp2;

        using (var h = TestHarness.FreshEngineWithSeed())
        {
            h.ReplayAllSignals("corvus");
            fp1 = h.GetIndex("corvus")!.Fingerprint;
        }
        using (var h = TestHarness.FreshEngineWithSeed())
        {
            h.ReplayAllSignals("corvus");
            fp2 = h.GetIndex("corvus")!.Fingerprint;
        }

        Assert.Equal(fp1, fp2);
        Assert.Equal(64, fp1.Length);
    }

    [Fact]
    [Trait("Class", "C")]
    public void C3_Meridian_Fingerprint_IsStable_AcrossTwoRuns()
    {
        string fp1, fp2;

        using (var h = TestHarness.FreshEngineWithSeed())
        {
            h.ReplayAllSignals("meridian");
            fp1 = h.GetIndex("meridian")!.Fingerprint;
        }
        using (var h = TestHarness.FreshEngineWithSeed())
        {
            h.ReplayAllSignals("meridian");
            fp2 = h.GetIndex("meridian")!.Fingerprint;
        }

        Assert.Equal(fp1, fp2);
        Assert.Equal(64, fp1.Length);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // CLASS D — IncrementalFull (incremental recompute == full recompute)
    // Uses incIdx.ComputedAt as 'now' so both paths share the same timestamp.
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    [Trait("Class", "D")]
    public void D1_Cloudwave_Incremental_Equals_Full_Recompute()
    {
        AssertIncrementalEqualsFullForVendor("cloudwave");
    }

    [Fact]
    [Trait("Class", "D")]
    public void D2_Corvus_Incremental_Equals_Full_Recompute()
    {
        AssertIncrementalEqualsFullForVendor("corvus");
    }

    [Fact]
    [Trait("Class", "D")]
    public void D3_Meridian_Incremental_Equals_Full_Recompute()
    {
        AssertIncrementalEqualsFullForVendor("meridian");
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // CLASS E — Decay
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    [Trait("Class", "E")]
    public void E1_Older_Belief_Weighs_Less_Than_Newer_Belief()
    {
        var profile = TestHelpers.LoadProfile();
        var decay   = new DecayEngine();
        var now     = new DateTimeOffset(2026, 6, 15, 10, 0, 0, TimeSpan.Zero);
        var entityId = Guid.NewGuid();

        var recent = MakeBelief(entityId, Dimension.Operational, "uptime_sla", 0.80,
            SourceTier.Verified, profile, now, createdAt: now.AddDays(-10));
        var older  = MakeBelief(entityId, Dimension.Operational, "uptime_sla", 0.80,
            SourceTier.Verified, profile, now, createdAt: now.AddDays(-50));

        var recentWithDecay = decay.WithCurrentFreshness(recent, profile, now);
        var olderWithDecay  = decay.WithCurrentFreshness(older,  profile, now);

        Assert.True(recentWithDecay.Confidence > olderWithDecay.Confidence,
            $"Recent conf={recentWithDecay.Confidence:F4} should exceed older conf={olderWithDecay.Confidence:F4}");
    }

    [Fact]
    [Trait("Class", "E")]
    public void E2_Freshness_Follows_HalfLife_Formula()
    {
        // freshness = 2^(-ageDays / halfLifeDays)
        var profile   = TestHelpers.LoadProfile();
        var decay     = new DecayEngine();
        var now       = new DateTimeOffset(2026, 6, 15, 10, 0, 0, TimeSpan.Zero);
        var entityId  = Guid.NewGuid();
        const double ageDays  = 45.0;
        const double halfLife = 90.0;   // Verified tier

        var belief = MakeBelief(entityId, Dimension.Financial, "payment_timeliness", 0.80,
            SourceTier.Verified, profile, now, createdAt: now.AddDays(-ageDays));

        var freshness = decay.ComputeFreshness(belief, profile, now);
        var expected  = Math.Pow(2.0, -ageDays / halfLife);   // ≈ 0.7071

        Assert.Equal(expected, freshness, 10);
    }

    [Fact]
    [Trait("Class", "E")]
    public void E3_Decay_Produces_Freshness_Below_1_For_Old_Belief()
    {
        var profile  = TestHelpers.LoadProfile();
        var decay    = new DecayEngine();
        var now      = new DateTimeOffset(2026, 6, 15, 10, 0, 0, TimeSpan.Zero);
        var entityId = Guid.NewGuid();

        // A belief 30 days old under Verified (90-day half-life)
        var belief = MakeBelief(entityId, Dimension.Operational, "uptime_sla", 0.80,
            SourceTier.Verified, profile, now, createdAt: now.AddDays(-30));

        var updated = decay.WithCurrentFreshness(belief, profile, now);

        Assert.True(updated.Freshness < 1.0,
            $"Freshness {updated.Freshness:F6} should be < 1.0 for a 30-day-old Verified belief");
        Assert.True(updated.Confidence < 0.95,
            $"Confidence {updated.Confidence:F6} should be < tier_weight=0.95 due to decay");
    }

    [Fact]
    [Trait("Class", "E")]
    public void E4_Empty_Dimension_Returns_Neutral_Score_Zero_Confidence()
    {
        var profile  = TestHelpers.LoadProfile();
        var rubric   = new RubricModule();
        var entityId = Guid.NewGuid();

        var score = rubric.ScoreDimension(entityId, Dimension.Strategic, [], profile);

        Assert.Equal(0.5,  score.Score);
        Assert.Equal(0.0,  score.Confidence);
        Assert.Empty(score.ContributingBeliefIds);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // CLASS F — RegressionPin
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    [Trait("Class", "F")]
    public void F1_Meridian_TwoSignal_Subset_IsHealthy()
    {
        // Meridian uses only 2 signals (Operational + Financial); expect Healthy + Maintain
        using var h = TestHarness.FreshEngineWithSeed();
        h.ReplayAllSignals("meridian");

        Assert.Equal(Band.Healthy,   h.GetIndex("meridian")!.Band);
        Assert.Equal(Stance.Maintain, h.GetPosture("meridian")!.Stance);
        Assert.True(h.GetIndex("meridian")!.Composite >= 0.65,
            $"Meridian composite {h.GetIndex("meridian")!.Composite:F4} should be >= 0.65 (Healthy threshold)");
    }

    [Fact]
    [Trait("Class", "F")]
    public void F2_Meridian_Fingerprint_Pin()
    {
        // Tripwire — if an intentional later change shifts a score, re-pin; bands/stances must not change.
        const string Pin = "72237da04d94ec26401c032c7877608f75f54f0f584c61b7fe3831ca04e00af4";

        using var h = TestHarness.FreshEngineWithSeed();
        h.ReplayAllSignals("meridian");
        var actual = h.GetIndex("meridian")!.Fingerprint;

        Assert.Equal(64, actual.Length);
        Assert.Equal(Pin, actual);
    }

    [Fact]
    [Trait("Class", "F")]
    public void F3_Cloudwave_Fingerprint_Pin()
    {
        // Tripwire — if an intentional later change shifts a score, re-pin; bands/stances must not change.
        const string Pin = "e5d0e9b99409ac2ef0799003e05f27ea90a0d2b524a88fe906ccd0d3722bd78f";

        using var h = TestHarness.FreshEngineWithSeed();
        h.ReplayAllSignals("cloudwave");
        var actual = h.GetIndex("cloudwave")!.Fingerprint;

        Assert.Equal(64, actual.Length);
        Assert.Equal(Pin, actual);
    }

    [Fact]
    [Trait("Class", "F")]
    public void F4_Corvus_Fingerprint_Pin()
    {
        // Tripwire — if an intentional later change shifts a score, re-pin; bands/stances must not change.
        const string Pin = "7e7cf0052779e8aaaa0f2bdc79995efd54dd3cd1bb4911ab8069ac15fd535e13";

        using var h = TestHarness.FreshEngineWithSeed();
        h.ReplayAllSignals("corvus");
        var actual = h.GetIndex("corvus")!.Fingerprint;

        Assert.Equal(64, actual.Length);
        Assert.Equal(Pin, actual);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Shared helpers
    // ═══════════════════════════════════════════════════════════════════════════

    private static void AssertIncrementalEqualsFullForVendor(string vendor)
    {
        using var h      = TestHarness.FreshEngineWithSeed();
        h.ReplayAllSignals(vendor);

        // Incremental result: built by sequential signal submission via IiFacade
        var incIdx   = h.GetIndex(vendor)!;
        var entityId = TestHarness.VendorIds[vendor];

        // Full recompute: same beliefs + same 'now' as the incremental index
        var beliefs = h.GetBeliefs(vendor);
        var now     = incIdx.ComputedAt;
        var decayed = beliefs.Select(b => h.Decay.WithCurrentFreshness(b, h.Profile, now)).ToList();

        var allDims = new[] { Dimension.Operational, Dimension.Experiential, Dimension.Financial, Dimension.Strategic };
        var scores  = allDims.ToDictionary(d => d, d =>
        {
            var db = decayed.Where(b => b.Dimension == d).ToList();
            return db.Count > 0
                ? h.Rubric.ScoreDimension(entityId, d, db, h.Profile)
                : new DimensionScore(entityId, d, 0.5, 0.0, []);
        });

        var fullIdx = h.Index.Aggregate(entityId, scores, decayed, null, h.Profile, now);

        Assert.Equal(incIdx.Fingerprint,                  fullIdx.Fingerprint);
        Assert.Equal(incIdx.Band,                         fullIdx.Band);
        Assert.Equal(incIdx.Composite,                    fullIdx.Composite,      5);
        Assert.Equal(incIdx.ConfidenceFloor,              fullIdx.ConfidenceFloor, 5);
    }

    private static Belief MakeBelief(
        Guid entityId, Dimension dim, string criterion, double value,
        SourceTier tier, SaasProfile profile, DateTimeOffset now,
        DateTimeOffset? createdAt = null)
    {
        var at         = createdAt ?? now;
        var tierWeight = profile.SourceTiers.TryGetValue(tier.ToString(), out var tc) ? tc.Weight : 0.0;
        var ageDays    = (now - at).TotalDays;
        var halfLife   = profile.HalfLifeDays.TryGetValue(tier.ToString(), out var hl) ? hl : 90;
        var freshness  = ageDays > 0 ? Math.Pow(2.0, -ageDays / halfLife) : 1.0;

        return new Belief(
            Id:            Guid.NewGuid(),
            EntityId:      entityId,
            Dimension:     dim,
            Criterion:     criterion,
            Value:         value,
            SourceTier:    tier,
            Confidence:    tierWeight * freshness,
            Freshness:     freshness,
            Derivation:    $"test:{criterion}",
            SourceSignals: [],
            Version:       1,
            SupersededBy:  null,
            CreatedAt:     at,
            TraceId:       Guid.NewGuid());
    }

    private static IReadOnlyDictionary<Dimension, DimensionScore> AllDimScores(
        Guid entityId, IReadOnlyList<Belief> beliefs, SaasProfile profile)
    {
        var rubric  = new RubricModule();
        var allDims = new[] { Dimension.Operational, Dimension.Experiential, Dimension.Financial, Dimension.Strategic };
        var byDim   = beliefs.GroupBy(b => b.Dimension).ToDictionary(g => g.Key, g => g.ToList());

        return allDims.ToDictionary(d => d, d =>
            byDim.TryGetValue(d, out var db)
                ? rubric.ScoreDimension(entityId, d, db, profile)
                : new DimensionScore(entityId, d, 0.5, 0.0, []));
    }

    private static IReadOnlyDictionary<Dimension, DimensionScore> CriticalScores(Guid entityId)
    {
        var allDims = new[] { Dimension.Operational, Dimension.Experiential, Dimension.Financial, Dimension.Strategic };
        return allDims.ToDictionary(d => d,
            d => new DimensionScore(entityId, d, 0.26, 0.90, []));
    }
}
