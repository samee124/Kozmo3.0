using Ii.Contracts;
using Ii.Decay;
using Ii.Index;
using Ii.Observation;
using Ii.Posture;
using Ii.Rubric;
using Ii.Spine;
using Km.Store;
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

    // ── T11: Cross-source contradiction — PRIMARY wins over REPORTED ──────────

    [Fact]
    [Trait("Class", "T")]
    public async Task T11_Contradiction_CrossSource_PrimaryWinsEmail()
    {
        // Vertex has payment_terms = Net 45 (PRIMARY, contract) and
        // payment_terms = Net 30 (REPORTED, email). REPORTED rank < PRIMARY rank
        // so no supersession → both beliefs are active → cross-source contradiction.
        var entityId = Guid.NewGuid();
        var profile  = TestHelpers.LoadProfile();

        using var store = new SqliteEntityStore("Data Source=:memory:", profile);
        var registry    = new EntityRegistry();
        registry.Register(entityId, "Vertex", renewalDate: null);

        var facade = new IiFacade(
            new ObservationModule(), new RubricModule(), new IndexModule(),
            new PostureModule(), new DecayEngine(), store, profile, registry, DemoClock.Fixed);

        var svc = new VendorFileWriteService(store, profile);

        var ts         = DemoClock.AsOf.AddMonths(-1);
        var primaryEvId = Guid.NewGuid();
        var emailEvId   = Guid.NewGuid();

        // PRIMARY: Net 45 from the signed contract
        var primaryBelief = await svc.WriteBeliefAsync(
            vendorId:            entityId,
            claimKey:            "payment_terms",
            dimension:           Dimension.Financial,
            criterion:           "payment_terms",
            rawValue:            45.0,
            tier:                SourceTier.Primary,
            extractorConfidence: 0.95,
            observedAt:          ts,
            provenance:          new BeliefProvenance(primaryEvId, "page:3 §8.1"),
            ingestedAt:          ts);

        // REPORTED: Net 30 from an email — lower rank, must NOT supersede PRIMARY
        var reportedBelief = await svc.WriteBeliefAsync(
            vendorId:            entityId,
            claimKey:            "payment_terms",
            dimension:           Dimension.Financial,
            criterion:           "payment_terms",
            rawValue:            30.0,
            tier:                SourceTier.Reported,
            extractorConfidence: 0.50,
            observedAt:          ts.AddDays(7),
            provenance:          new BeliefProvenance(emailEvId, "message_ref:email-vendor-pm"),
            ingestedAt:          ts.AddDays(7));

        // Both beliefs must be active — neither supersedes the other
        var active = await store.GetCurrentBeliefsAsync(entityId);
        var ptBeliefs = active.Where(b => b.ClaimKey == "payment_terms").ToList();
        Assert.Equal(2, ptBeliefs.Count);
        Assert.All(ptBeliefs, b => Assert.Null(b.SupersededBy));

        // PRIMARY value is still 45.0
        var primary = ptBeliefs.Single(b => b.SourceTier == SourceTier.Primary);
        Assert.Equal(45.0, primary.Value);

        // GetReasoningTrailAsync triggers ComputeMeta → cross-source contradiction must fire
        var trail = await facade.GetReasoningTrailAsync(entityId);

        Assert.NotNull(trail);
        Assert.True(trail!.Meta.HasValue, "Expected Meta to be populated after vendor-file writes");

        var meta = trail.Meta!.Value;

        Assert.True(
            meta.Contradictions.Any(c => c.Description.Contains("payment_terms", StringComparison.OrdinalIgnoreCase)),
            "Expected a cross-source contradiction for claim_key payment_terms");

        var xsContra = meta.Contradictions
            .First(c => c.Description.Contains("payment_terms", StringComparison.OrdinalIgnoreCase));

        // Description must name both tiers
        Assert.Contains("Primary",  xsContra.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Reported", xsContra.Description, StringComparison.OrdinalIgnoreCase);

        // Both belief IDs must be referenced
        Assert.Equal(2, xsContra.ConflictingBeliefIds.Count);
        Assert.Contains(primaryBelief.Id,  xsContra.ConflictingBeliefIds);
        Assert.Contains(reportedBelief.Id, xsContra.ConflictingBeliefIds);

        // The PRIMARY belief remains fully active (not marked superseded)
        var allHistory = await store.GetBeliefHistoryAsync(entityId);
        var storedPrimary = allHistory.Single(b => b.Id == primaryBelief.Id);
        Assert.Null(storedPrimary.SupersededBy);
    }

    // ── T12: Scored multi-source must NOT raise a cross-source contradiction ──

    [Fact]
    [Trait("Class", "T")]
    public async Task T12_Contradiction_NotRaised_OnScoredMultiSource()
    {
        // Scored claim_keys (csat, sla_uptime, …) are fused by the Rubric; two beliefs
        // from different tiers representing different measurements is normal aggregation.
        // Only structural claim_keys (payment_terms, renewal_date, …) are discrete facts
        // where two conflicting values cannot both be correct.

        var profile  = TestHelpers.LoadProfile();
        using var store = new SqliteEntityStore("Data Source=:memory:", profile);
        var registry    = new EntityRegistry();
        var svc         = new VendorFileWriteService(store, profile);

        var scoredId     = Guid.NewGuid();  // receives csat VERIFIED + csat REPORTED
        var structuralId = Guid.NewGuid();  // receives payment_terms PRIMARY + REPORTED

        registry.Register(scoredId,     "Scored entity",     renewalDate: null);
        registry.Register(structuralId, "Structural entity", renewalDate: null);

        var facade = new IiFacade(
            new ObservationModule(), new RubricModule(), new IndexModule(),
            new PostureModule(), new DecayEngine(), store, profile, registry, DemoClock.Fixed);

        var ts = DemoClock.AsOf.AddMonths(-1);

        // ── Part 1: scored claim_key — VERIFIED csat from CSV + REPORTED from email ──
        // REPORTED rank (2) < VERIFIED rank (3) → no supersession → both beliefs active.
        await svc.WriteBeliefAsync(
            scoredId, "csat", Dimension.Experiential, "csat",
            0.80, SourceTier.Verified, 1.0, ts,
            new BeliefProvenance(Guid.NewGuid(), "csv:row2"), ts);

        await svc.WriteBeliefAsync(
            scoredId, "csat", Dimension.Experiential, "csat",
            0.55, SourceTier.Reported, 1.0, ts.AddDays(7),
            new BeliefProvenance(Guid.NewGuid(), "message_ref:email-csat-001"), ts.AddDays(7));

        // Confirm both csat beliefs are active (neither superseded the other)
        var activeScored = (await store.GetCurrentBeliefsAsync(scoredId))
            .Where(b => b.ClaimKey == "csat").ToList();
        Assert.Equal(2, activeScored.Count);

        var scoredTrail = await facade.GetReasoningTrailAsync(scoredId);
        Assert.NotNull(scoredTrail);
        Assert.True(scoredTrail!.Meta.HasValue);

        // Zero contradictions — scored multi-source is Rubric fusion, not a conflict
        Assert.Empty(scoredTrail.Meta!.Value.Contradictions);

        // ── Part 2: structural claim_key — PRIMARY payment_terms + REPORTED still fires ──
        await svc.WriteBeliefAsync(
            structuralId, "payment_terms", Dimension.Financial, "payment_terms",
            45.0, SourceTier.Primary, 0.95, ts,
            new BeliefProvenance(Guid.NewGuid(), "page:3 §8.1"), ts);

        await svc.WriteBeliefAsync(
            structuralId, "payment_terms", Dimension.Financial, "payment_terms",
            30.0, SourceTier.Reported, 0.50, ts.AddDays(7),
            new BeliefProvenance(Guid.NewGuid(), "message_ref:email-pm"), ts.AddDays(7));

        var structuralTrail = await facade.GetReasoningTrailAsync(structuralId);
        Assert.NotNull(structuralTrail);
        Assert.True(structuralTrail!.Meta.HasValue);

        Assert.True(
            structuralTrail.Meta!.Value.Contradictions.Any(c =>
                c.Description.Contains("payment_terms", StringComparison.OrdinalIgnoreCase)),
            "Expected a cross-source contradiction for structural claim_key payment_terms");
    }

    // ── T13: Structural supersession must NOT raise a delta-threshold contradiction ──

    [Fact]
    [Trait("Class", "T")]
    public async Task T13_StructuralSupersession_NoDeltaContradiction()
    {
        // A Quote (REPORTED) annual_value=145000 is superseded by a Contract (PRIMARY)
        // annual_value=155000.  Delta=10000 >> 0.30 threshold, but annual_value is a
        // structural claim; the threshold is designed for [0,1] scores only.
        // Supersession by a higher tier is correct versioning — zero contradictions expected.
        var entityId = Guid.NewGuid();
        var profile  = TestHelpers.LoadProfile();

        using var store = new SqliteEntityStore("Data Source=:memory:", profile);
        var registry    = new EntityRegistry();
        registry.Register(entityId, "Borealis-T13", renewalDate: null);

        var facade = new IiFacade(
            new ObservationModule(), new RubricModule(), new IndexModule(),
            new PostureModule(), new DecayEngine(), store, profile, registry, DemoClock.Fixed);

        var svc = new VendorFileWriteService(store, profile);
        var ts  = DemoClock.AsOf.AddMonths(-3);

        // Quote REPORTED: annual_value = 145,000 (lower tier, written first)
        await svc.WriteBeliefAsync(
            vendorId:            entityId,
            claimKey:            "annual_value",
            dimension:           Dimension.Financial,
            criterion:           "annual_value",
            rawValue:            145000.0,
            tier:                SourceTier.Reported,
            extractorConfidence: 1.0,
            observedAt:          ts,
            provenance:          new BeliefProvenance(Guid.NewGuid(), "page:1 header"),
            ingestedAt:          ts);

        // Contract PRIMARY: annual_value = 155,000 — supersedes the Quote (rank 4 > rank 2)
        await svc.WriteBeliefAsync(
            vendorId:            entityId,
            claimKey:            "annual_value",
            dimension:           Dimension.Financial,
            criterion:           "annual_value",
            rawValue:            155000.0,
            tier:                SourceTier.Primary,
            extractorConfidence: 1.0,
            observedAt:          ts.AddMonths(6),
            provenance:          new BeliefProvenance(Guid.NewGuid(), "page:2 §2.1"),
            ingestedAt:          ts.AddMonths(6));

        // Only the PRIMARY belief should be active; the Reported one is superseded
        var active = await store.GetCurrentBeliefsAsync(entityId);
        var avBeliefs = active.Where(b => b.ClaimKey == "annual_value").ToList();
        Assert.Single(avBeliefs);
        Assert.Equal(SourceTier.Primary, avBeliefs[0].SourceTier);

        var trail = await facade.GetReasoningTrailAsync(entityId);
        Assert.NotNull(trail);
        Assert.True(trail!.Meta.HasValue);

        Assert.Empty(trail.Meta!.Value.Contradictions);
    }

    // ── T14: Scored supersession with large delta DOES raise a contradiction ─────

    [Fact]
    [Trait("Class", "T")]
    public async Task T14_ScoredSupersession_LargeDeltaRaisesContradiction()
    {
        // A VERIFIED sla_uptime=0.95 is superseded by a PRIMARY sla_uptime=0.10.
        // sla_uptime is a scored claim_key; delta=0.85 >= 0.30 → contradiction must fire.
        var entityId = Guid.NewGuid();
        var profile  = TestHelpers.LoadProfile();

        using var store = new SqliteEntityStore("Data Source=:memory:", profile);
        var registry    = new EntityRegistry();
        registry.Register(entityId, "Scored-T14", renewalDate: null);

        var facade = new IiFacade(
            new ObservationModule(), new RubricModule(), new IndexModule(),
            new PostureModule(), new DecayEngine(), store, profile, registry, DemoClock.Fixed);

        var svc = new VendorFileWriteService(store, profile);
        var ts  = DemoClock.AsOf.AddDays(-5);

        // VERIFIED sla_uptime = 0.95 (healthy), written first
        await svc.WriteBeliefAsync(
            vendorId:            entityId,
            claimKey:            "sla_uptime",
            dimension:           Dimension.Operational,
            criterion:           "uptime_sla",
            rawValue:            0.95,
            tier:                SourceTier.Verified,
            extractorConfidence: 1.0,
            observedAt:          ts,
            provenance:          new BeliefProvenance(Guid.NewGuid(), "csv:row1"),
            ingestedAt:          ts);

        // PRIMARY sla_uptime = 0.10 (critical incident) — supersedes VERIFIED (rank 4 > rank 3)
        // delta = |0.10 - 0.95| = 0.85 >= 0.30 → contradiction
        await svc.WriteBeliefAsync(
            vendorId:            entityId,
            claimKey:            "sla_uptime",
            dimension:           Dimension.Operational,
            criterion:           "uptime_sla",
            rawValue:            0.10,
            tier:                SourceTier.Primary,
            extractorConfidence: 1.0,
            observedAt:          ts.AddDays(1),
            provenance:          new BeliefProvenance(Guid.NewGuid(), "page:4 §5.1"),
            ingestedAt:          ts.AddDays(1));

        // PRIMARY must be the only active belief
        var active = await store.GetCurrentBeliefsAsync(entityId);
        var uptimeBeliefs = active.Where(b => b.ClaimKey == "sla_uptime").ToList();
        Assert.Single(uptimeBeliefs);
        Assert.Equal(SourceTier.Primary, uptimeBeliefs[0].SourceTier);

        var trail = await facade.GetReasoningTrailAsync(entityId);
        Assert.NotNull(trail);
        Assert.True(trail!.Meta.HasValue);

        // Scored supersession with delta=0.85 must raise a contradiction
        Assert.True(
            trail.Meta!.Value.Contradictions.Any(c =>
                c.Description.Contains("uptime_sla", StringComparison.OrdinalIgnoreCase)
                || c.Description.Contains("sla_uptime", StringComparison.OrdinalIgnoreCase)),
            "Expected a contradiction for scored sla_uptime supersession with delta=0.85");
    }
}
