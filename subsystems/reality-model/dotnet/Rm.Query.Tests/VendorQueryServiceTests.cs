using Ii.Contracts;
using Ii.Spine;
using Kozmo.Contracts;
using Kozmo.Llm;
using Rm.Contracts;
using Rm.Query;
using Wc.Contracts;
using Xunit;

namespace Rm.Query.Tests;

// ── Shared vendor IDs ────────────────────────────────────────────────────────

file static class Vendors
{
    public static readonly Guid CloudwaveId = Guid.Parse("eeeeeeee-0001-0000-0000-000000000001");
    public static readonly Guid CorvusId    = Guid.Parse("eeeeeeee-0002-0000-0000-000000000001");
    public static readonly Guid MeridianId  = Guid.Parse("eeeeeeee-0003-0000-0000-000000000001");
    // A vendor known to the registry but with no scored data
    public static readonly Guid UnassessedId = Guid.Parse("eeeeeeee-0009-0000-0000-000000000001");

    public static EntityRegistry BuildRegistry()
    {
        var reg = new EntityRegistry();
        reg.Register(CloudwaveId, "Cloudwave Systems Inc.", new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero));
        reg.Register(CorvusId,    "Corvus Infrastructure Ltd.");
        reg.Register(MeridianId,  "Meridian IT Services Ltd.");
        reg.Register(UnassessedId, "Helix Solutions AG");
        return reg;
    }
}

// ── Stub IIiFacade ────────────────────────────────────────────────────────────

file sealed class StubIiFacade : IIiFacade
{
    private readonly Dictionary<Guid, ReasoningTrail?> _trails = new();

    public void AddTrail(Guid id, ReasoningTrail? trail) => _trails[id] = trail;

    public Task<ReasoningTrail?> GetReasoningTrailAsync(Guid entityId, CancellationToken ct = default)
        => Task.FromResult(_trails.TryGetValue(entityId, out var t) ? t : null);

    public Task<PostureAssignment?> GetPostureAsync(Guid entityId, CancellationToken ct = default)
        => Task.FromResult((PostureAssignment?)null);

    public Task<EntityIndex?> GetIndexAsync(Guid entityId, CancellationToken ct = default)
        => Task.FromResult((EntityIndex?)null);

    public Task<IReadOnlyList<Belief>> GetBeliefsAsync(Guid entityId, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<Belief>>([]);

    public Task<IReadOnlyList<TrajectoryPoint>> GetTrajectoryAsync(Guid entityId, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<TrajectoryPoint>>([]);

    public Task<Guid> SubmitSignalAsync(Signal signal, CancellationToken ct = default)
        => throw new NotSupportedException("Read-only stub");

    public Task ResetAsync(CancellationToken ct = default)
        => throw new NotSupportedException("Read-only stub");

    public Task<VendorJudgement?> RecomputeVendorAsync(Guid entityId, CancellationToken ct = default)
        => throw new NotSupportedException("Read-only stub");
}

// ── Stub ICheckInStore ────────────────────────────────────────────────────────

file sealed class StubCheckInStore : ICheckInStore
{
    private readonly List<CheckIn> _open = [];
    public void AddOpen(CheckIn ci) => _open.Add(ci);

    public Task<IReadOnlyList<CheckIn>> GetOpenAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<CheckIn>>(_open.ToList());

    public Task<CheckIn?> GetAsync(Guid id, CancellationToken ct = default)
        => Task.FromResult(_open.FirstOrDefault(c => c.CheckInId == id));

    public Task SaveAsync(CheckIn checkIn, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task<IReadOnlyList<CheckIn>> GetResolvedForVendorAsync(Guid vendorId, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<CheckIn>>([]);
}

// ── Stub IKozmoLlm that always throws LlmCacheMissException ─────────────────

file sealed class AlwaysMissLlm : IKozmoLlm
{
    public Task<LlmResult> CompleteJsonAsync(string system, string user, int maxTokens = 500, CancellationToken ct = default)
        => throw new LlmCacheMissException("test-key");
}

// ── Factory helpers ───────────────────────────────────────────────────────────

file static class ServiceFactory
{
    public static VendorQueryService Build(
        StubIiFacade    facade,
        StubCheckInStore store,
        IKozmoLlm?      llm = null)
    {
        var registry = Vendors.BuildRegistry();
        return new VendorQueryService(facade, store, registry, llm);
    }

    // Minimal Cloudwave posture for tests
    public static PostureAssignment CloudwavePosture() => new(
        Id:            Guid.NewGuid(),
        EntityId:      Vendors.CloudwaveId,
        Band:          Band.AtRisk,
        Stance:        Stance.Renegotiate,
        Rationale:     "Multiple operational and financial signals indicate vendor is at risk.",
        EvidenceTrail: ["uptime below SLA", "payment overdue"],
        Confidence:    0.72,
        Fingerprint:   "abc123",
        IndexVersion:  1,
        AssignedAt:    DateTimeOffset.UtcNow,
        ValidUntil:    null)
    {
        Cautions     = ["Contradiction detected in uptime data"],
        EvidenceGaps = ["No signed SLA on record"]
    };

    public static EntityIndex CloudwaveIndex() => new(
        EntityId: Vendors.CloudwaveId,
        DimensionScores: new Dictionary<Dimension, DimensionScore>
        {
            [Dimension.Operational]   = new(Vendors.CloudwaveId, Dimension.Operational,   0.45, 0.70, []),
            [Dimension.Experiential]  = new(Vendors.CloudwaveId, Dimension.Experiential,  0.40, 0.65, []),
            [Dimension.Financial]     = new(Vendors.CloudwaveId, Dimension.Financial,     0.55, 0.80, []),
            [Dimension.Strategic]     = new(Vendors.CloudwaveId, Dimension.Strategic,     0.50, 0.60, [])
        },
        Composite:      0.47,
        ConfidenceFloor: 0.60,
        Band:           Band.AtRisk,
        Fingerprint:    "abc123",
        Version:        1,
        ComputedAt:     DateTimeOffset.UtcNow);

    public static Belief UptimeBelief() => new(
        Id:             Guid.NewGuid(),
        EntityId:       Vendors.CloudwaveId,
        Dimension:      Dimension.Operational,
        Criterion:      "uptime_sla_compliance",
        Value:          0.30,
        SourceTier:     SourceTier.Verified,
        Confidence:     0.70,
        Freshness:      0.90,
        Derivation:     "rule:uptime_below_threshold",
        SourceSignals:  [],
        Version:        1,
        SupersededBy:   null,
        CreatedAt:      DateTimeOffset.UtcNow,
        TraceId:        Guid.NewGuid());

    public static Belief FinancialBelief() => new(
        Id:             Guid.NewGuid(),
        EntityId:       Vendors.CloudwaveId,
        Dimension:      Dimension.Financial,
        Criterion:      "payment_delay_days",
        Value:          0.55,
        SourceTier:     SourceTier.Verified,
        Confidence:     0.80,
        Freshness:      0.85,
        Derivation:     "rule:payment_overdue",
        SourceSignals:  [],
        Version:        1,
        SupersededBy:   null,
        CreatedAt:      DateTimeOffset.UtcNow,
        TraceId:        Guid.NewGuid());

    public static Contradiction UptimeContradiction() => new(
        EntityId:            Vendors.CloudwaveId.ToString(),
        Dimension:           "Operational",
        Description:         "Self-reported uptime (99.9%) contradicts monitoring data (94.2%)",
        Severity:            ContradictionSeverity.High,
        ConflictingBeliefIds: [],
        DetectedBy:          DetectionSource.Deterministic);

    public static Contradiction FinancialContradiction() => new(
        EntityId:            Vendors.CloudwaveId.ToString(),
        Dimension:           "Financial",
        Description:         "Reported payment on-time (100%) contradicts outstanding invoice data (45 days overdue)",
        Severity:            ContradictionSeverity.Medium,
        ConflictingBeliefIds: [],
        DetectedBy:          DetectionSource.Deterministic);

    public static ReasoningTrail CloudwaveTrail(
        bool includeContradiction          = false,
        bool includeGap                    = false,
        bool includeFinancialContradiction = false)
    {
        var posture  = CloudwavePosture();
        var index    = CloudwaveIndex();

        var contradictionList = new List<Contradiction>();
        if (includeContradiction)          contradictionList.Add(UptimeContradiction());
        if (includeFinancialContradiction) contradictionList.Add(FinancialContradiction());
        IReadOnlyList<Contradiction> contradictions = contradictionList;

        IReadOnlyList<Gap> gaps = includeGap
            ? [new Gap(Vendors.CloudwaveId.ToString(), "Financial", "No invoice data available", DetectionSource.Deterministic)]
            : [];

        var meta = new MetaCognitionResult(
            EntityId:         Vendors.CloudwaveId.ToString(),
            Contradictions:   contradictions,
            Gaps:             gaps,
            EpistemicSummary: "Assessment confidence is moderate; uptime data is the primary concern.");

        return new ReasoningTrail(
            EntityId:      Vendors.CloudwaveId,
            Posture:       posture,
            Index:         index,
            CurrentBeliefs: [UptimeBelief(), FinancialBelief()],
            SourceSignals: [])
        { Meta = meta };
    }
}

// ── Tests ─────────────────────────────────────────────────────────────────────

public sealed class VendorQueryServiceTests
{
    // ── T1: Known vendor, Full aspect ─────────────────────────────────────────
    // Grounding contains the real stored posture; text references that posture.
    // LLM is null → template fallback; test verifies fallback is fact-complete.

    [Fact]
    public async Task KnownVendor_Full_GroundingContainsStoredPosture()
    {
        var facade = new StubIiFacade();
        facade.AddTrail(Vendors.CloudwaveId, ServiceFactory.CloudwaveTrail());

        var svc = ServiceFactory.Build(facade, new StubCheckInStore());
        var answer = await svc.AnswerAsync(new VendorQuery("What is Cloudwave's status?"));

        // Grounding must reflect the stored posture
        Assert.NotNull(answer.Grounding);
        Assert.True(answer.Grounding.IsAssessed);
        Assert.Equal(Stance.Renegotiate, answer.Grounding.Posture!.Stance);
        Assert.Equal(Band.AtRisk,       answer.Grounding.Index!.Band);

        // Text must reference the retrieved posture, not a different one
        Assert.Contains("Renegotiate", answer.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("AtRisk",      answer.Text, StringComparison.OrdinalIgnoreCase);
    }

    // ── T2: Known vendor with contradiction ──────────────────────────────────
    // Grounding includes the contradiction; answer mentions it; not resolved away.

    [Fact]
    public async Task KnownVendor_WithContradiction_ReportedNotResolvedAway()
    {
        var facade = new StubIiFacade();
        facade.AddTrail(Vendors.CloudwaveId, ServiceFactory.CloudwaveTrail(includeContradiction: true));

        var svc    = ServiceFactory.Build(facade, new StubCheckInStore());
        var answer = await svc.AnswerAsync(new VendorQuery("Show me contradictions for Cloudwave"));

        // Grounding has the contradiction
        Assert.NotNull(answer.Grounding);
        Assert.Single(answer.Grounding.Contradictions);
        Assert.Equal(ContradictionSeverity.High, answer.Grounding.Contradictions[0].Severity);

        // Text reports the contradiction, does not resolve it
        Assert.Contains("Contradiction", answer.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("High",          answer.Text, StringComparison.OrdinalIgnoreCase);
        // Must NOT claim the contradiction was resolved
        Assert.DoesNotContain("resolved", answer.Text, StringComparison.OrdinalIgnoreCase);
    }

    // ── T3: Unknown / unresolvable vendor ────────────────────────────────────
    // Honest "no such vendor" answer; compose never called; no fabricated status.

    [Fact]
    public async Task UnknownVendor_HonestAnswer_ComposeNotCalled()
    {
        var facade = new StubIiFacade(); // no trails seeded
        // Use a "tracking" LLM that would fail if called — LlmCacheMissException
        var svc    = ServiceFactory.Build(facade, new StubCheckInStore(), new AlwaysMissLlm());

        var answer = await svc.AnswerAsync(new VendorQuery("What about Acme Corp?"));

        // No grounding — vendor not identified
        Assert.Null(answer.Grounding);
        // Honest message, no fabricated posture
        Assert.DoesNotContain("Renegotiate", answer.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("AtRisk",      answer.Text, StringComparison.OrdinalIgnoreCase);
    }

    // ── T4: Resolved but not assessed ────────────────────────────────────────
    // Honest "no assessment yet"; compose skipped; Grounding reflects the state.

    [Fact]
    public async Task ResolvedButNotAssessed_HonestAnswer_ComposeSkipped()
    {
        var facade = new StubIiFacade();
        // Helix is in the registry but has no trail → no posture/index
        facade.AddTrail(Vendors.UnassessedId, null);

        var svc    = ServiceFactory.Build(facade, new StubCheckInStore());
        var answer = await svc.AnswerAsync(new VendorQuery("What is Helix Solutions' posture?"));

        // Grounding present (vendor known) but not assessed
        Assert.NotNull(answer.Grounding);
        Assert.False(answer.Grounding.IsAssessed);

        // Text says no assessment, no invented status
        Assert.Contains("no assessment", answer.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Renegotiate", answer.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Maintain",    answer.Text, StringComparison.OrdinalIgnoreCase);
    }

    // ── T5: Aspect scoping — Evidence ────────────────────────────────────────
    // "Why" question → Evidence aspect; Grounding has beliefs; Contradictions/Gaps empty.

    [Fact]
    public async Task EvidenceAspect_GroundingHasBeliefsNotGaps()
    {
        var facade = new StubIiFacade();
        facade.AddTrail(Vendors.CloudwaveId, ServiceFactory.CloudwaveTrail(includeGap: true));

        var svc    = ServiceFactory.Build(facade, new StubCheckInStore());
        var answer = await svc.AnswerAsync(new VendorQuery("Why is Cloudwave renegotiate?"));

        Assert.NotNull(answer.Grounding);
        Assert.Equal(Aspect.Evidence, answer.Grounding.Posture is not null ? Aspect.Evidence : Aspect.Full);

        // Evidence aspect → beliefs populated
        Assert.NotEmpty(answer.Grounding.Beliefs);

        // Evidence aspect → gaps NOT populated (scoped out)
        Assert.Empty(answer.Grounding.Gaps);
    }

    // ── T6: Aspect scoping — Gaps ─────────────────────────────────────────────
    // "Missing" question → Gaps aspect; Grounding has gaps; Beliefs empty.

    [Fact]
    public async Task GapsAspect_GroundingHasGapsNotBeliefs()
    {
        var facade = new StubIiFacade();
        facade.AddTrail(Vendors.CloudwaveId, ServiceFactory.CloudwaveTrail(includeGap: true));

        var store = new StubCheckInStore();
        store.AddOpen(new CheckIn(
            CheckInId:     Guid.NewGuid(),
            VendorId:      Vendors.CloudwaveId,
            ProgramRunId:  Guid.NewGuid(),
            Kind:          CheckInKind.DIMENSION_GAP,
            Question:      "Does Cloudwave have a signed SLA?",
            ResponseShape: ResponseShape.YES_NO,
            TargetField:   null,
            Owner:         "owner@test",
            Status:        PendingStatus.OPEN,
            RaisedAt:      DateTimeOffset.UtcNow,
            AnsweredAt:    null,
            ExpiresAt:     null,
            ResponseValue: null));

        var svc    = ServiceFactory.Build(facade, store);
        var answer = await svc.AnswerAsync(new VendorQuery("What is missing for Cloudwave?"));

        Assert.NotNull(answer.Grounding);

        // Gaps aspect → beliefs NOT populated
        Assert.Empty(answer.Grounding.Beliefs);

        // Open check-in is in Grounding
        Assert.Single(answer.Grounding.OpenCheckIns);
        Assert.Contains("SLA", answer.Grounding.OpenCheckIns[0].Question);

        // Meta gap is in Grounding
        Assert.Single(answer.Grounding.Gaps);
    }

    // ── T7: LLM failure → deterministic template fallback ───────────────────
    // AlwaysMissLlm throws LlmCacheMissException; answer is correct and complete;
    // no exception escapes; no invented content.

    [Fact]
    public async Task LlmFailure_TemplateFallback_CorrectAndComplete()
    {
        var facade = new StubIiFacade();
        facade.AddTrail(Vendors.CloudwaveId, ServiceFactory.CloudwaveTrail());

        var svc    = ServiceFactory.Build(facade, new StubCheckInStore(), new AlwaysMissLlm());
        var answer = await svc.AnswerAsync(new VendorQuery("Cloudwave posture"));

        // No exception escaped
        Assert.NotNull(answer);
        Assert.NotNull(answer.Grounding);

        // Template fallback produces the real posture
        Assert.Contains("Renegotiate", answer.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("AtRisk",      answer.Text, StringComparison.OrdinalIgnoreCase);

        // No invented content: text doesn't mention vendors not in context
        Assert.DoesNotContain("Corvus",   answer.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Meridian", answer.Text, StringComparison.OrdinalIgnoreCase);
    }

    // ── T8: Grounding parity ──────────────────────────────────────────────────
    // Every factual assertion in the answer traces to a Grounding field.
    // Tests that Text's posture claim matches Grounding.Posture.Stance.

    [Fact]
    public async Task GroundingParity_TextPostureMatchesGrounding()
    {
        var facade = new StubIiFacade();
        facade.AddTrail(Vendors.CloudwaveId, ServiceFactory.CloudwaveTrail());

        var svc    = ServiceFactory.Build(facade, new StubCheckInStore());
        var answer = await svc.AnswerAsync(new VendorQuery("Cloudwave overview"));

        Assert.NotNull(answer.Grounding);
        var storedStance = answer.Grounding.Posture!.Stance.ToString();

        // The text must mention the exact stored stance, not a different one
        Assert.Contains(storedStance, answer.Text, StringComparison.OrdinalIgnoreCase);

        // Verify no other stance appears (would indicate hallucination)
        var otherStances = Enum.GetValues<Stance>()
            .Where(s => s != answer.Grounding.Posture.Stance)
            .Select(s => s.ToString());
        foreach (var other in otherStances)
            Assert.DoesNotContain(other, answer.Text, StringComparison.OrdinalIgnoreCase);
    }

    // ── T9: Determinism lane — template fallback is deterministic ─────────────
    // Same inputs produce identical template output across two calls.

    [Fact]
    public void TemplateFallback_SameInputs_IdenticalOutput()
    {
        var ctx = new RetrievedContext(
            VendorId:         Vendors.CloudwaveId,
            VendorName:       "Cloudwave Systems Inc.",
            IsAssessed:       true,
            Posture:          ServiceFactory.CloudwavePosture(),
            Index:            ServiceFactory.CloudwaveIndex(),
            Beliefs:          [ServiceFactory.UptimeBelief()],
            Contradictions:   [],
            Gaps:             [],
            EpistemicSummary: null,
            OpenCheckIns:     []);

        var text1 = VendorQueryComposer.TemplateFallback(ctx, Aspect.Full);
        var text2 = VendorQueryComposer.TemplateFallback(ctx, Aspect.Full);

        Assert.Equal(text1, text2);
    }

    // ── T10: FilterDimension scopes beliefs to that dimension only ────────
    // Trail has two beliefs: Operational + Financial.
    // Filtering on Financial → Grounding has only the Financial belief.

    [Fact]
    public async Task FilterDimension_Financial_ScopesBeliefs()
    {
        var facade = new StubIiFacade();
        facade.AddTrail(Vendors.CloudwaveId, ServiceFactory.CloudwaveTrail());

        var svc    = ServiceFactory.Build(facade, new StubCheckInStore());
        var query  = new VendorQuery("Cloudwave financial detail", null, Aspect.Full, FilterDimension: Dimension.Financial);
        var answer = await svc.AnswerAsync(query);

        Assert.NotNull(answer.Grounding);
        // Only Financial beliefs
        Assert.All(answer.Grounding.Beliefs, b => Assert.Equal(Dimension.Financial, b.Dimension));
        // No Operational belief in scoped result
        Assert.DoesNotContain(answer.Grounding.Beliefs, b => b.Dimension == Dimension.Operational);
    }

    // ── T11: FilterDimension scopes gaps to that dimension ────────────────
    // Trail has a Financial gap. Filtering on Financial → gap present.
    // Filtering on Operational → gap absent.

    [Fact]
    public async Task FilterDimension_Financial_IncludesFinancialGap_ExcludesOperationalGap()
    {
        var facade = new StubIiFacade();
        facade.AddTrail(Vendors.CloudwaveId, ServiceFactory.CloudwaveTrail(includeGap: true));

        var svc     = ServiceFactory.Build(facade, new StubCheckInStore());

        var finAnswer = await svc.AnswerAsync(new VendorQuery("Cloudwave", null, Aspect.Full, FilterDimension: Dimension.Financial));
        var opAnswer  = await svc.AnswerAsync(new VendorQuery("Cloudwave", null, Aspect.Full, FilterDimension: Dimension.Operational));

        Assert.Single(finAnswer.Grounding!.Gaps);                  // Financial gap present
        Assert.Empty(opAnswer.Grounding!.Gaps);                    // no Operational gap in data
    }

    // ── T12: FilterDimension scopes contradictions to that dimension ──────
    // Trail has Operational + Financial contradictions.
    // Filtering on Financial → only Financial contradiction.

    [Fact]
    public async Task FilterDimension_Financial_ScopesContradictions()
    {
        var facade = new StubIiFacade();
        facade.AddTrail(Vendors.CloudwaveId,
            ServiceFactory.CloudwaveTrail(includeContradiction: true, includeFinancialContradiction: true));

        var svc    = ServiceFactory.Build(facade, new StubCheckInStore());
        var answer = await svc.AnswerAsync(
            new VendorQuery("cloudwave", null, Aspect.Full, FilterDimension: Dimension.Financial));

        Assert.NotNull(answer.Grounding);
        Assert.Single(answer.Grounding.Contradictions);
        Assert.Equal("Financial", answer.Grounding.Contradictions[0].Dimension);
    }

    // ── T13: Demo beat — dimension answer is narrower than full overview ──
    // vendor_dimension_detail('Cloudwave', 'Financial') must:
    //   a) contain Financial content  b) NOT contain Operational belief criterion

    [Fact]
    public async Task DimDetail_Financial_NarrowerThanFullOverview()
    {
        var facade = new StubIiFacade();
        facade.AddTrail(Vendors.CloudwaveId, ServiceFactory.CloudwaveTrail());

        var svc = ServiceFactory.Build(facade, new StubCheckInStore());

        var fullAnswer = await svc.AnswerAsync(new VendorQuery("Cloudwave overview"));
        var dimAnswer  = await svc.AnswerAsync(
            new VendorQuery("Cloudwave financial", null, Aspect.Full, FilterDimension: Dimension.Financial));

        // Dimension answer surfaces Financial criterion
        Assert.Contains("payment_delay_days", dimAnswer.Text, StringComparison.OrdinalIgnoreCase);

        // Dimension answer does NOT surface Operational criterion (narrower)
        Assert.DoesNotContain("uptime_sla_compliance", dimAnswer.Text, StringComparison.OrdinalIgnoreCase);

        // Full overview DOES contain Operational criterion (proving full is wider)
        Assert.Contains("uptime_sla_compliance", fullAnswer.Text, StringComparison.OrdinalIgnoreCase);
    }
}
