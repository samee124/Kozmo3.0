using Ii.Contracts;
using Ii.Spine;
using Kozmo.Contracts;
using Kozmo.Llm;
using Xunit;

namespace Ii.Tests;

/// <summary>
/// Class T — MetaCognition (B4 deterministic path + B4a calibrated confidence).
///
/// T1  Contradiction fires when superseding belief value diverges ≥ 0.30 from predecessor.
///     PostureAssignment.Cautions is populated.
/// T2  Gap fires when a dimension has no current evidence.
///     PostureAssignment.EvidenceGaps is populated (3 gaps from single Operational signal).
/// T3  Control — all four dimensions covered, no large value swings → both lists empty.
/// T4  Anchor surfaces in ReasoningTrail.Meta.EpistemicSummary when confidence anchor fires.
/// T5  Determinism — two identical runs produce identical Cautions and EvidenceGaps.
///
/// B4a — calibrated confidence + anchor provenance (PART 1 + PART 2):
/// T6  One gap → posture confidence is strictly below ConfidenceFloor by exactly PerGapPenalty.
/// T7  No penalty — zero gaps + zero contradictions → confidence equals ConfidenceFloor.
/// T8  Additive — gap + contradiction penalty both apply; total is their sum.
/// T9  Clamp — heavy combined penalty never produces negative confidence.
/// T10 Anchor provenance — anchored belief exposes raw confidence and predecessor id/tier in trail.
/// </summary>
public sealed class MetaCognitionTests
{
    private static readonly Guid CwId  = Guid.Parse("eeeeeeee-0001-0000-0000-000000000001");
    private static readonly Guid CorId = Guid.Parse("eeeeeeee-0002-0000-0000-000000000001");

    // ── Signal helpers ────────────────────────────────────────────────────────

    /// <summary>MonitoringPlatform uptime signal → Operational/uptime_sla, Verified.</summary>
    private static Signal MakeUptimeSignal(Guid entityId, double uptimePct, DateTimeOffset? at = null) =>
        new Signal(
            Id:           Guid.NewGuid(),
            EntityId:     entityId,
            CustomerId:   Guid.Parse("cccccccc-0000-0000-0000-000000000001"),
            SourceSystem: SourceSystem.MonitoringPlatform,
            ExternalId:   $"mon-{Guid.NewGuid():N}",
            Payload:      new Dictionary<string, object?> { ["uptime_pct"] = uptimePct },
            ObservedAt:   at ?? new DateTimeOffset(2026, 5, 14, 8, 0, 0, TimeSpan.Zero),
            ReceivedAt:   (at ?? new DateTimeOffset(2026, 5, 14, 8, 0, 0, TimeSpan.Zero)).AddSeconds(30),
            TraceId:      Guid.NewGuid());

    /// <summary>Signal 12 — Corvus CSM free-text (same body as fixtures/signals.json).</summary>
    private static Signal MakeSignal12() => new Signal(
        Id:           Guid.NewGuid(),
        EntityId:     CorId,
        CustomerId:   Guid.Parse("cccccccc-0000-0000-0000-000000000001"),
        SourceSystem: SourceSystem.HumanReport,
        ExternalId:   "hr-cor-2026-0605-csm-001",
        Payload:      new Dictionary<string, object?>
        {
            ["body"] = "Support ticket response times from Corvus have been consistently above 48 hours over the past month. Three critical incidents were not resolved within SLA. The team is losing confidence in the platform's reliability and we are considering escalating this to executive level."
        },
        ObservedAt:   new DateTimeOffset(2026, 6, 5, 9, 0, 0, TimeSpan.Zero),
        ReceivedAt:   new DateTimeOffset(2026, 6, 5, 9, 10, 0, TimeSpan.Zero),
        TraceId:      Guid.NewGuid());

    private static CachingLlmClient MakeReplayClient() =>
        new CachingLlmClient(TestHelpers.FindLlmCachePath(), recordMode: false);

    // ── T1: Contradiction fires ───────────────────────────────────────────────

    [Fact]
    [Trait("Class", "T")]
    public void T1_ContradictionFires_WhenValueDivergesAboveThreshold()
    {
        using var h = TestHarness.FreshEngineWithSeed(clock: DemoClock.Fixed);

        // First: uptime 99.5 → score 1.00 (Verified)
        h.Ingest(MakeUptimeSignal(CwId, 99.5,
            new DateTimeOffset(2026, 5, 14, 8, 0, 0, TimeSpan.Zero)));
        // Second: uptime 50.0 → score 0.10 (Verified), supersedes first — Δ = 0.90 ≥ 0.30
        h.Ingest(MakeUptimeSignal(CwId, 50.0,
            new DateTimeOffset(2026, 5, 15, 8, 0, 0, TimeSpan.Zero)));

        var posture = h.GetPosture("cloudwave")!;
        Assert.True(posture.Cautions.Count > 0,
            "Expected at least one contradiction caution when value diverges by 0.90");
        Assert.Contains(posture.Cautions, c =>
            c.Contains("Operational", StringComparison.OrdinalIgnoreCase));
    }

    // ── T2: Gap fires ─────────────────────────────────────────────────────────

    [Fact]
    [Trait("Class", "T")]
    public void T2_GapFires_WhenDimensionsLackEvidence()
    {
        using var h = TestHarness.FreshEngineWithSeed(clock: DemoClock.Fixed);

        // Only Operational coverage — Experiential, Financial, Strategic are absent
        h.Ingest(MakeUptimeSignal(CwId, 99.5));

        var posture = h.GetPosture("cloudwave")!;
        Assert.Equal(3, posture.EvidenceGaps.Count);
    }

    // ── T3: Control — no spurious contradictions or gaps ─────────────────────

    [Fact]
    [Trait("Class", "T")]
    public void T3_Control_AllDimensionsCovered_EmptyContradictionsAndGaps()
    {
        using var h = TestHarness.FreshEngineWithSeed(clock: DemoClock.Fixed);
        // All four Cloudwave dimensions covered; no large value swings between supersessions
        h.ReplayAllSignals("cloudwave");

        var posture = h.GetPosture("cloudwave")!;
        Assert.Empty(posture.Cautions);
        Assert.Empty(posture.EvidenceGaps);
    }

    // ── T4: Anchor surfaces in ReasoningTrail.Meta ───────────────────────────

    [Fact]
    [Trait("Class", "T")]
    public void T4_AnchorFires_SurfacedInReasoningTrailMeta()
    {
        // Corvus + S12: S12 (Reported) supersedes S5 (Verified, uptime_sla).
        // Confidence anchor raises S12's effective confidence to S5's decayed level.
        // EpistemicSummary must describe the anchor — no silent confidence boosts.
        using var h = TestHarness.FreshEngineWithSeed(clock: DemoClock.Fixed, llm: MakeReplayClient());
        h.ReplayAllSignals("corvus");
        h.Ingest(MakeSignal12());

        var trail = h.GetReasoningTrail("corvus");
        Assert.NotNull(trail);
        Assert.True(trail!.Meta.HasValue, "Expected Meta to be populated");
        Assert.False(string.IsNullOrEmpty(trail.Meta!.Value.EpistemicSummary),
            "Expected EpistemicSummary to be non-empty when anchor fires");
        Assert.Contains("anchor", trail.Meta.Value.EpistemicSummary,
            StringComparison.OrdinalIgnoreCase);
    }

    // ── T5: Determinism ───────────────────────────────────────────────────────

    [Fact]
    [Trait("Class", "T")]
    public void T5_Determinism_SameSignals_SameMetaOutput()
    {
        IReadOnlyList<string> cautions1, gaps1;
        IReadOnlyList<string> cautions2, gaps2;

        using (var h = TestHarness.FreshEngineWithSeed(clock: DemoClock.Fixed))
        {
            h.Ingest(MakeUptimeSignal(CwId, 99.5,
                new DateTimeOffset(2026, 5, 14, 8, 0, 0, TimeSpan.Zero)));
            h.Ingest(MakeUptimeSignal(CwId, 50.0,
                new DateTimeOffset(2026, 5, 15, 8, 0, 0, TimeSpan.Zero)));
            var p = h.GetPosture("cloudwave")!;
            cautions1 = p.Cautions;
            gaps1     = p.EvidenceGaps;
        }

        using (var h = TestHarness.FreshEngineWithSeed(clock: DemoClock.Fixed))
        {
            h.Ingest(MakeUptimeSignal(CwId, 99.5,
                new DateTimeOffset(2026, 5, 14, 8, 0, 0, TimeSpan.Zero)));
            h.Ingest(MakeUptimeSignal(CwId, 50.0,
                new DateTimeOffset(2026, 5, 15, 8, 0, 0, TimeSpan.Zero)));
            var p = h.GetPosture("cloudwave")!;
            cautions2 = p.Cautions;
            gaps2     = p.EvidenceGaps;
        }

        Assert.Equal(cautions1.Count, cautions2.Count);
        for (var i = 0; i < cautions1.Count; i++)
            Assert.Equal(cautions1[i], cautions2[i]);

        Assert.Equal(gaps1.Count, gaps2.Count);
    }

    // ── T6: Gap penalty reduces confidence below ConfidenceFloor (B4a PART 1) ─

    [Fact]
    [Trait("Class", "T")]
    public void T6_OneGap_ConfidenceBelowFloor_ByExactlyPerGapPenalty()
    {
        // Ingest 3 of 4 Cloudwave dimensions (skip Strategic) → 1 gap
        using var h = TestHarness.FreshEngineWithSeed(clock: DemoClock.Fixed);
        var sigs = TestHarness.SignalsFor("cloudwave");
        h.Ingest(sigs[0]); // Operational
        h.Ingest(sigs[1]); // Experiential
        h.Ingest(sigs[2]); // Financial — no Strategic → 1 gap

        var index   = h.GetIndex("cloudwave")!;
        var posture = h.GetPosture("cloudwave")!;
        var perGap  = h.Profile.Bands.PerGapPenalty;

        var expected = Math.Clamp(index.ConfidenceFloor - perGap, 0.0, 0.95);

        Assert.Single(posture.EvidenceGaps);
        Assert.Equal(expected, posture.Confidence, precision: 5);
        Assert.True(posture.Confidence < index.ConfidenceFloor,
            $"Expected confidence {posture.Confidence:F5} < ConfidenceFloor {index.ConfidenceFloor:F5}");
    }

    // ── T7: No gaps, no contradictions → confidence equals ConfidenceFloor ───

    [Fact]
    [Trait("Class", "T")]
    public void T7_NoPenalty_ConfidenceEqualsConfidenceFloor()
    {
        using var h = TestHarness.FreshEngineWithSeed(clock: DemoClock.Fixed);
        h.ReplayAllSignals("cloudwave"); // all 4 dims, no supersession contradictions

        var index   = h.GetIndex("cloudwave")!;
        var posture = h.GetPosture("cloudwave")!;

        Assert.Empty(posture.Cautions);
        Assert.Empty(posture.EvidenceGaps);
        Assert.Equal(index.ConfidenceFloor, posture.Confidence, precision: 5);
    }

    // ── T8: Gap + contradiction penalties are additive ─────────────────────

    [Fact]
    [Trait("Class", "T")]
    public void T8_GapAndContradiction_PenaltyIsAdditive()
    {
        // 2 uptime signals → 1 contradiction (Δ=0.90) + only Operational covered → 3 gaps
        using var h = TestHarness.FreshEngineWithSeed(clock: DemoClock.Fixed);
        h.Ingest(MakeUptimeSignal(CwId, 99.5,
            new DateTimeOffset(2026, 5, 14, 8, 0, 0, TimeSpan.Zero)));
        h.Ingest(MakeUptimeSignal(CwId, 50.0,
            new DateTimeOffset(2026, 5, 15, 8, 0, 0, TimeSpan.Zero)));

        var index   = h.GetIndex("cloudwave")!;
        var posture = h.GetPosture("cloudwave")!;
        var bands   = h.Profile.Bands;

        Assert.Single(posture.Cautions);     // 1 contradiction
        Assert.Equal(3, posture.EvidenceGaps.Count); // 3 gaps (xUnit2013 not applicable — count=3 not 1)

        var expected = Math.Clamp(
            index.ConfidenceFloor
                - bands.PerContradictionPenalty * 1
                - bands.PerGapPenalty * 3,
            0.0, 0.95);

        Assert.Equal(expected, posture.Confidence, precision: 5);
    }

    // ── T9: Clamp — heavy penalty never produces negative confidence ────────

    [Fact]
    [Trait("Class", "T")]
    public void T9_ClampFloor_HeavyPenalty_ConfidenceNeverNegative()
    {
        // 1 contradiction + 3 gaps with a low ConfidenceFloor scenario
        using var h = TestHarness.FreshEngineWithSeed(clock: DemoClock.Fixed);
        h.Ingest(MakeUptimeSignal(CwId, 99.5,
            new DateTimeOffset(2026, 5, 14, 8, 0, 0, TimeSpan.Zero)));
        h.Ingest(MakeUptimeSignal(CwId, 50.0,
            new DateTimeOffset(2026, 5, 15, 8, 0, 0, TimeSpan.Zero)));

        var posture = h.GetPosture("cloudwave")!;

        Assert.True(posture.Confidence >= 0.0,
            $"Confidence must never go negative; got {posture.Confidence}");
        Assert.True(posture.Confidence <= 0.95,
            $"Confidence must be capped at 0.95; got {posture.Confidence}");
    }

    // ── T10: Anchor provenance exposed in ReasoningTrail.CurrentBeliefs ─────

    [Fact]
    [Trait("Class", "T")]
    public void T10_AnchorProvenance_ExposedInReasoningTrail()
    {
        // Corvus + S12: S12 (Reported uptime_sla=0.20) supersedes S5 (Verified uptime_sla=0.25).
        // The anchor raises S12's effective confidence to S5's decayed level.
        // The belief in the trail must expose: raw < effective, predecessor id + tier.
        using var h = TestHarness.FreshEngineWithSeed(clock: DemoClock.Fixed, llm: MakeReplayClient());
        h.ReplayAllSignals("corvus");
        h.Ingest(MakeSignal12());

        var trail = h.GetReasoningTrail("corvus");
        Assert.NotNull(trail);

        var anchored = trail!.CurrentBeliefs
            .FirstOrDefault(b => b.Dimension == Dimension.Operational
                              && b.Criterion  == "uptime_sla"
                              && b.AnchorRawConfidence.HasValue);

        Assert.NotNull(anchored);
        Assert.True(anchored!.AnchorRawConfidence!.Value < anchored.Confidence,
            $"Expected raw {anchored.AnchorRawConfidence:F5} < effective {anchored.Confidence:F5}");
        Assert.NotNull(anchored.AnchorPredecessorId);
        Assert.NotNull(anchored.AnchorPredecessorTier);
        Assert.Equal(SourceTier.Verified, anchored.AnchorPredecessorTier!.Value);
    }
}
