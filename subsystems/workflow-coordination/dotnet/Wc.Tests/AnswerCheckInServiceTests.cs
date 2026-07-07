using Ig.Contracts;
using Ii.Contracts;
using Kozmo.Contracts;
using Kozmo.Contracts.Config;
using Kozmo.Contracts.Interfaces;
using Km.Store;
using Wc.CheckIn;
using Wc.Contracts;
using Xunit;

namespace Wc.Tests;

using CheckIn = global::Wc.Contracts.CheckIn;

public sealed class AnswerCheckInServiceTests
{
    private static readonly Guid     VendorId  = new("AAAAAAAA-0000-0000-0000-000000000001");
    private static readonly Guid     RunId     = new("BBBBBBBB-0000-0000-0000-000000000001");
    private static readonly DateTimeOffset Now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Answered = Now.AddHours(2);

    // ── helpers ───────────────────────────────────────────────────────────────

    private static CheckIn OpenCheckIn(Guid id, ResponseShape shape) => new CheckIn(
        CheckInId:     id,
        VendorId:      VendorId,
        ProgramRunId:  RunId,
        Kind:          CheckInKind.DIMENSION_GAP,
        Question:      "Test question",
        ResponseShape: shape,
        TargetField:   shape == ResponseShape.TYPED_VALUE ? "contract_ref" : null,
        Owner:         "owner@test",
        Status:        PendingStatus.OPEN,
        RaisedAt:      Now,
        AnsweredAt:    null,
        ExpiresAt:     null,
        ResponseValue: null);

    // ── A. Simulated transport surfaces OPEN check-in in pending list ─────────

    [Fact]
    public async Task Send_DoesNotRemoveCheckInFromPendingList()
    {
        var store     = new InMemoryCheckInStore();
        var transport = new InAppCheckInTransport();
        var id        = Guid.NewGuid();
        var ci        = OpenCheckIn(id, ResponseShape.YES_NO);
        await store.SaveAsync(ci);

        await transport.SendAsync([ci]);

        var open = await store.GetOpenAsync();
        Assert.Single(open);
        Assert.Equal(id, open[0].CheckInId);
        Assert.Equal(PendingStatus.OPEN, open[0].Status);
    }

    // ── B. Valid YES_NO responses ─────────────────────────────────────────────

    [Fact]
    public async Task Answer_YesNo_True_RecordsAnswered()
    {
        var store = new InMemoryCheckInStore();
        var svc   = new AnswerCheckInService();
        var id    = Guid.NewGuid();
        await store.SaveAsync(OpenCheckIn(id, ResponseShape.YES_NO));

        var result = await svc.AnswerAsync(id, "true", Answered, store);

        Assert.Equal(AnswerOutcome.Ok, result.Outcome);
        Assert.NotNull(result.Updated);
        Assert.Equal(PendingStatus.ANSWERED, result.Updated!.Status);
        Assert.Equal("true",  result.Updated.ResponseValue);
        Assert.Equal(Answered, result.Updated.AnsweredAt);
    }

    [Fact]
    public async Task Answer_YesNo_False_RecordsAnswered()
    {
        var store = new InMemoryCheckInStore();
        var svc   = new AnswerCheckInService();
        var id    = Guid.NewGuid();
        await store.SaveAsync(OpenCheckIn(id, ResponseShape.YES_NO));

        var result = await svc.AnswerAsync(id, "false", Answered, store);

        Assert.Equal(AnswerOutcome.Ok,          result.Outcome);
        Assert.Equal(PendingStatus.ANSWERED,    result.Updated!.Status);
        Assert.Equal("false",                   result.Updated.ResponseValue);
    }

    // ── C. Valid TYPED_VALUE response ─────────────────────────────────────────

    [Fact]
    public async Task Answer_TypedValue_RecordsAnswered()
    {
        var store = new InMemoryCheckInStore();
        var svc   = new AnswerCheckInService();
        var id    = Guid.NewGuid();
        await store.SaveAsync(OpenCheckIn(id, ResponseShape.TYPED_VALUE));

        var result = await svc.AnswerAsync(id, "ACQ-2024-001", Answered, store);

        Assert.Equal(AnswerOutcome.Ok,       result.Outcome);
        Assert.Equal(PendingStatus.ANSWERED, result.Updated!.Status);
        Assert.Equal("ACQ-2024-001",         result.Updated.ResponseValue);
    }

    // ── D. Valid STATUS_SELECT response ───────────────────────────────────────

    [Fact]
    public async Task Answer_StatusSelect_RecordsAnswered()
    {
        var store = new InMemoryCheckInStore();
        var svc   = new AnswerCheckInService();
        var id    = Guid.NewGuid();
        await store.SaveAsync(OpenCheckIn(id, ResponseShape.STATUS_SELECT));

        var result = await svc.AnswerAsync(id, "Active", Answered, store);

        Assert.Equal(AnswerOutcome.Ok,       result.Outcome);
        Assert.Equal(PendingStatus.ANSWERED, result.Updated!.Status);
        Assert.Equal("Active",               result.Updated.ResponseValue);
    }

    // ── E. Unknown checkin_id ─────────────────────────────────────────────────

    [Fact]
    public async Task Answer_UnknownId_ReturnsNotFound()
    {
        var store  = new InMemoryCheckInStore();
        var svc    = new AnswerCheckInService();

        var result = await svc.AnswerAsync(Guid.NewGuid(), "true", Answered, store);

        Assert.Equal(AnswerOutcome.NotFound, result.Outcome);
        Assert.Null(result.Updated);
    }

    // ── F. Already-ANSWERED checkin_id ───────────────────────────────────────

    [Fact]
    public async Task Answer_AlreadyAnswered_ReturnsAlreadyAnswered()
    {
        var store = new InMemoryCheckInStore();
        var svc   = new AnswerCheckInService();
        var id    = Guid.NewGuid();
        var ci    = OpenCheckIn(id, ResponseShape.YES_NO);
        // Pre-answer it directly
        await store.SaveAsync(ci with { Status = PendingStatus.ANSWERED, ResponseValue = "true" });

        var result = await svc.AnswerAsync(id, "false", Answered, store);

        Assert.Equal(AnswerOutcome.AlreadyAnswered, result.Outcome);
        // Original response_value must be unchanged
        var stored = await store.GetAsync(id);
        Assert.Equal("true", stored!.ResponseValue);
    }

    // ── G. Shape mismatch — text submitted to a YES_NO check-in ──────────────

    [Fact]
    public async Task Answer_ShapeMismatch_YesNoRejectsText()
    {
        var store = new InMemoryCheckInStore();
        var svc   = new AnswerCheckInService();
        var id    = Guid.NewGuid();
        await store.SaveAsync(OpenCheckIn(id, ResponseShape.YES_NO));

        // A free-text value that a TYPED_VALUE form would submit, applied to a YES_NO check-in
        var result = await svc.AnswerAsync(id, "contract number ACQ-001", Answered, store);

        Assert.Equal(AnswerOutcome.ShapeMismatch, result.Outcome);
        // Check-in must still be OPEN — no state change on rejection
        var stored = await store.GetAsync(id);
        Assert.Equal(PendingStatus.OPEN, stored!.Status);
        Assert.Null(stored.ResponseValue);
    }
}

// ── ProcessAnswerAsync tests ──────────────────────────────────────────────────

public sealed class ProcessAnswerAsyncTests
{
    private static readonly Guid   VendorId = new("FFFFFFFF-0000-0000-0000-000000000001");
    private static readonly Guid   RunId    = new("FFFFFFFF-0000-0000-0000-000000000002");
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    // Minimal SaasProfile with one numeric rubric criterion (uptime_sla: 95–100 → 1.0, else 0.5)
    // and one enum criterion (renewal_intent: YES → 0.8) for TYPED_VALUE / STATUS_SELECT tests.
    private static readonly SaasProfile TestProfile = new(
        ConfigVersion:       "test",
        Dimensions:          new Dictionary<string, DimensionDefinition>(),
        ScoringRubric: new Dictionary<string, CriterionRubric>
        {
            ["uptime_sla"] = new CriterionRubric(
                Type: "numeric",
                NumericThresholds: new[]
                {
                    new RubricThreshold(95.0, 100.0, 1.0),
                    new RubricThreshold(85.0,  95.0, 0.5)
                },
                EnumScores: null),
            ["renewal_intent"] = new CriterionRubric(
                Type: "enum",
                NumericThresholds: null,
                EnumScores: new Dictionary<string, double> { ["YES"] = 0.8, ["NO"] = 0.2 })
        },
        DimensionWeights:    new Dictionary<string, double>(),
        Bands:               new BandsConfig(0.6, 0.4, 0.5, 0.1, 0.05),
        PostureRules:        new List<PostureRule>(),
        SourceTiers:         new Dictionary<string, SourceTierConfig>(),
        ClassificationRules: new List<ClassificationRule>(),
        HalfLifeDays:        new Dictionary<string, int>(),
        EntityResolution:    new EntityResolutionConfig("exact", 0.85, new Dictionary<string, string>()))
    {
        ClaimKeyCatalogue = new Dictionary<string, ClaimKeyDefinition>
        {
            // claim_key "sla_uptime" maps to rubric criterion "uptime_sla"
            ["sla_uptime"] = new ClaimKeyDefinition(
                ClaimClass:     "scored",
                ValueType:      "percent",
                Dimension:      "Operational",
                TypicalTier:    "VERIFIED",
                HalfLifeDays:   30,
                DimensionWeight: 0.25)
            { RubricCriterion = "uptime_sla" }
        }
    };

    private static CheckIn OpenDimGap(Guid id, ResponseShape shape, string? targetField = null) =>
        new(CheckInId:     id,
            VendorId:      VendorId,
            ProgramRunId:  RunId,
            Kind:          CheckInKind.DIMENSION_GAP,
            Question:      "Does the vendor have a documented uptime SLA?",
            ResponseShape: shape,
            TargetField:   targetField,
            Owner:         "owner@test",
            Status:        PendingStatus.OPEN,
            RaisedAt:      Now,
            AnsweredAt:    null,
            ExpiresAt:     null,
            ResponseValue: null);

    // ── PA1. YES answer → belief written at SourceTier.Reported, recompute called, PROCESSED ─

    [Fact]
    public async Task ProcessAnswer_Yes_WritesBelief_RecomputesCalled_StatusProcessed()
    {
        var checkInStore = new InMemoryCheckInStore();
        var entityStore  = new InMemoryEntityStore();
        var facade       = new TrackingFacade();
        var id           = Guid.NewGuid();
        await checkInStore.SaveAsync(OpenDimGap(id, ResponseShape.YES_NO));

        var svc    = new AnswerCheckInService();
        var result = await svc.ProcessAnswerAsync(
            id, "true", Now,
            checkInStore,
            new VendorFileWriteService(entityStore, TestProfile),
            TestProfile, facade,
            new InMemoryIdentityRegistry());

        Assert.Equal(AnswerOutcome.Ok, result.Outcome);

        // Exactly one belief written at SourceTier.Reported with rawValue=1.0
        Assert.Single(entityStore.AllBeliefs);
        var belief = entityStore.AllBeliefs[0];
        Assert.Equal(SourceTier.Reported, belief.SourceTier);
        Assert.Equal(1.0, belief.Value);
        Assert.Equal(VendorId, belief.EntityId);

        // RecomputeVendorAsync called exactly once for the right vendor
        Assert.Single(facade.RecomputedVendors);
        Assert.Equal(VendorId, facade.RecomputedVendors[0]);

        // Status is PROCESSED
        var stored = await checkInStore.GetAsync(id);
        Assert.Equal(PendingStatus.PROCESSED, stored!.Status);
        Assert.Equal("true", stored.ResponseValue);
    }

    // ── PA2. NO answer → belief written with rawValue=0.0 ────────────────────

    [Fact]
    public async Task ProcessAnswer_No_WritesBelief_RawValueZero()
    {
        var checkInStore = new InMemoryCheckInStore();
        var entityStore  = new InMemoryEntityStore();
        var facade       = new TrackingFacade();
        var id           = Guid.NewGuid();
        await checkInStore.SaveAsync(OpenDimGap(id, ResponseShape.YES_NO));

        var svc    = new AnswerCheckInService();
        var result = await svc.ProcessAnswerAsync(
            id, "false", Now,
            checkInStore,
            new VendorFileWriteService(entityStore, TestProfile),
            TestProfile, facade,
            new InMemoryIdentityRegistry());

        Assert.Equal(AnswerOutcome.Ok, result.Outcome);
        Assert.Single(entityStore.AllBeliefs);
        Assert.Equal(0.0, entityStore.AllBeliefs[0].Value);
        Assert.Single(facade.RecomputedVendors);
        var stored = await checkInStore.GetAsync(id);
        Assert.Equal(PendingStatus.PROCESSED, stored!.Status);
    }

    // ── PA3. UNKNOWN → no belief, no recompute, Status=PROCESSED ─────────────

    [Fact]
    public async Task ProcessAnswer_Unknown_NoBelief_NoRecompute_StatusProcessed()
    {
        var checkInStore = new InMemoryCheckInStore();
        var entityStore  = new InMemoryEntityStore();
        var facade       = new TrackingFacade();
        var id           = Guid.NewGuid();
        await checkInStore.SaveAsync(OpenDimGap(id, ResponseShape.YES_NO));

        var svc    = new AnswerCheckInService();
        var result = await svc.ProcessAnswerAsync(
            id, null, Now,
            checkInStore,
            new VendorFileWriteService(entityStore, TestProfile),
            TestProfile, facade,
            new InMemoryIdentityRegistry());

        Assert.Equal(AnswerOutcome.Ok, result.Outcome);
        Assert.Empty(entityStore.AllBeliefs);
        Assert.Empty(facade.RecomputedVendors);
        var stored = await checkInStore.GetAsync(id);
        Assert.Equal(PendingStatus.PROCESSED, stored!.Status);
    }

    // ── PA4. TYPED_VALUE out of rubric domain → no belief, still PROCESSED ────
    //
    // When the value can't be banded (e.g. out of the numeric domain), the belief
    // is skipped but the check-in is still closed as PROCESSED. The answer is recorded.

    [Fact]
    public async Task ProcessAnswer_TypedValue_OutOfDomain_NoBelief_StatusProcessed()
    {
        var checkInStore = new InMemoryCheckInStore();
        var entityStore  = new InMemoryEntityStore();
        var facade       = new TrackingFacade();
        var id           = Guid.NewGuid();
        // TargetField = "sla_uptime" (rubric_criterion = "uptime_sla"; domain 85-100)
        await checkInStore.SaveAsync(OpenDimGap(id, ResponseShape.TYPED_VALUE, targetField: "sla_uptime"));

        var svc    = new AnswerCheckInService();
        // "50" is below domain min (85) → ScoreFromRubric returns null → no belief written
        var result = await svc.ProcessAnswerAsync(
            id, "50", Now,
            checkInStore,
            new VendorFileWriteService(entityStore, TestProfile),
            TestProfile, facade,
            new InMemoryIdentityRegistry());

        Assert.Equal(AnswerOutcome.Ok, result.Outcome);
        Assert.Empty(entityStore.AllBeliefs);
        // Recompute is still called (belief gap, but vendor state may have changed elsewhere)
        var stored = await checkInStore.GetAsync(id);
        Assert.Equal(PendingStatus.PROCESSED, stored!.Status);
    }

    // ── PA5. TYPED_VALUE in rubric domain → belief written with rubric score ──

    [Fact]
    public async Task ProcessAnswer_TypedValue_InDomain_WritesBelief_WithRubricScore()
    {
        var checkInStore = new InMemoryCheckInStore();
        var entityStore  = new InMemoryEntityStore();
        var facade       = new TrackingFacade();
        var id           = Guid.NewGuid();
        await checkInStore.SaveAsync(OpenDimGap(id, ResponseShape.TYPED_VALUE, targetField: "sla_uptime"));

        var svc    = new AnswerCheckInService();
        // "99.5" is in domain [95, 100] → score = 1.0
        var result = await svc.ProcessAnswerAsync(
            id, "99.5", Now,
            checkInStore,
            new VendorFileWriteService(entityStore, TestProfile),
            TestProfile, facade,
            new InMemoryIdentityRegistry());

        Assert.Equal(AnswerOutcome.Ok, result.Outcome);
        Assert.Single(entityStore.AllBeliefs);
        Assert.Equal(1.0, entityStore.AllBeliefs[0].Value);
        Assert.Equal(SourceTier.Reported, entityStore.AllBeliefs[0].SourceTier);
    }

    // ── PA6. Double-call idempotency → second call returns AlreadyAnswered ────

    [Fact]
    public async Task ProcessAnswer_SecondCall_ReturnsAlreadyAnswered_NoDuplicateBelief()
    {
        var checkInStore = new InMemoryCheckInStore();
        var entityStore  = new InMemoryEntityStore();
        var facade       = new TrackingFacade();
        var id           = Guid.NewGuid();
        await checkInStore.SaveAsync(OpenDimGap(id, ResponseShape.YES_NO));

        var svc     = new AnswerCheckInService();
        var writeSvc = new VendorFileWriteService(entityStore, TestProfile);
        var registry = new InMemoryIdentityRegistry();

        // First call — should succeed
        var r1 = await svc.ProcessAnswerAsync(id, "true", Now, checkInStore, writeSvc, TestProfile, facade, registry);
        Assert.Equal(AnswerOutcome.Ok, r1.Outcome);

        // Second call — check-in is now PROCESSED, not OPEN → AlreadyAnswered
        var r2 = await svc.ProcessAnswerAsync(id, "true", Now, checkInStore, writeSvc, TestProfile, facade, registry);
        Assert.Equal(AnswerOutcome.AlreadyAnswered, r2.Outcome);

        // Belief written exactly once, recompute called exactly once
        Assert.Single(entityStore.AllBeliefs);
        Assert.Single(facade.RecomputedVendors);
    }

    // ── PA7. WriteBeliefAsync throws → status stays OPEN, no recompute ────────

    [Fact]
    public async Task ProcessAnswer_WriteThrows_StatusStaysOpen_NoRecompute()
    {
        var checkInStore  = new InMemoryCheckInStore();
        var throwingStore = new ThrowingEntityStore();
        var facade        = new TrackingFacade();
        var id            = Guid.NewGuid();
        await checkInStore.SaveAsync(OpenDimGap(id, ResponseShape.YES_NO));

        var svc = new AnswerCheckInService();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.ProcessAnswerAsync(
                id, "true", Now,
                checkInStore,
                new VendorFileWriteService(throwingStore, TestProfile),
                TestProfile, facade,
                new InMemoryIdentityRegistry()));

        // Status must still be OPEN — no partial write succeeded
        var stored = await checkInStore.GetAsync(id);
        Assert.Equal(PendingStatus.OPEN, stored!.Status);

        // No recompute triggered
        Assert.Empty(facade.RecomputedVendors);
    }

    // ── PA8. Belief write succeeds, RecomputeVendorAsync throws → OPEN ────────
    //
    // The belief is durably written (it will be superseded on retry), but the
    // PROCESSED stamp never runs — status stays OPEN so the answer is retryable.
    // This is the scenario that matters most for demo correctness: a written
    // belief with a band/posture that never moved because recompute was skipped
    // would show the wrong stance on screen.

    [Fact]
    public async Task ProcessAnswer_RecomputeThrows_StatusStaysOpen_BeliefAlreadyWritten()
    {
        var checkInStore = new InMemoryCheckInStore();
        var entityStore  = new InMemoryEntityStore();
        var facade       = new ThrowingRecomputeFacade();
        var id           = Guid.NewGuid();
        await checkInStore.SaveAsync(OpenDimGap(id, ResponseShape.YES_NO));

        var svc = new AnswerCheckInService();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.ProcessAnswerAsync(
                id, "true", Now,
                checkInStore,
                new VendorFileWriteService(entityStore, TestProfile),
                TestProfile, facade,
                new InMemoryIdentityRegistry()));

        // Belief was written before the throw (write-before-stamp ordering)
        Assert.Single(entityStore.AllBeliefs);

        // Status must still be OPEN — the PROCESSED stamp never ran
        var stored = await checkInStore.GetAsync(id);
        Assert.Equal(PendingStatus.OPEN, stored!.Status);
    }

    // ── PA-IC-throw. IDENTITY_CONFIRM: merge throws → lands ANSWERED, retryable ─
    //
    // ProcessCheckInService.ProcessAsync requires ANSWERED status as a precondition
    // (it returns NotAnswered for OPEN check-ins). The check-in must be stamped
    // ANSWERED before the merge — fixing this ordering requires changing
    // ProcessCheckInService, which is out of scope. This test documents the
    // failure mode: a mid-merge throw leaves the check-in ANSWERED (not PROCESSED),
    // which ProcessCheckInService can pick up and complete on a direct retry.
    // ANSWERED is "not yet processed" — the vendor profile has not been updated.

    [Fact]
    public async Task ProcessAnswer_IdentityConfirm_MergeThrows_StatusIsAnswered_NotProcessed()
    {
        var checkInStore     = new InMemoryCheckInStore();
        var throwingRegistry = new ThrowingIdentityRegistry();
        var facade           = new TrackingFacade();
        var id               = Guid.NewGuid();

        var ci = new CheckIn(
            CheckInId:     id,
            VendorId:      VendorId,
            ProgramRunId:  RunId,
            Kind:          CheckInKind.IDENTITY_CONFIRM,
            Question:      "Are Acme Corp and Acme Inc the same vendor?",
            ResponseShape: ResponseShape.YES_NO,
            TargetField:   null,
            Owner:         "owner@test",
            Status:        PendingStatus.OPEN,
            RaisedAt:      Now,
            AnsweredAt:    null,
            ExpiresAt:     null,
            ResponseValue: null);
        await checkInStore.SaveAsync(ci);

        var svc = new AnswerCheckInService();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.ProcessAnswerAsync(
                id, "true", Now,
                checkInStore,
                new VendorFileWriteService(new InMemoryEntityStore(), TestProfile),
                TestProfile, facade,
                throwingRegistry));

        // Check-in is ANSWERED (the human's response is recorded), not PROCESSED
        // (the vendor profile has not been updated). ProcessCheckInService.ProcessAsync
        // can be called directly on this check-in to complete the merge on retry.
        var stored = await checkInStore.GetAsync(id);
        Assert.Equal(PendingStatus.ANSWERED, stored!.Status);
        Assert.Equal("true", stored.ResponseValue);

        // No recompute was triggered — the merge never completed
        Assert.Empty(facade.RecomputedVendors);
    }
}

/// <summary>IIiFacade that throws on RecomputeVendorAsync to simulate recompute failure.</summary>
internal sealed class ThrowingRecomputeFacade : IIiFacade
{
    public Task<VendorJudgement?> RecomputeVendorAsync(Guid entityId, CancellationToken ct = default)
        => throw new InvalidOperationException("Simulated recompute failure.");

    public Task<Guid>                           SubmitSignalAsync(Signal signal, CancellationToken ct = default)      => throw new NotSupportedException();
    public Task<PostureAssignment?>              GetPostureAsync(Guid entityId, CancellationToken ct = default)        => throw new NotSupportedException();
    public Task<EntityIndex?>                   GetIndexAsync(Guid entityId, CancellationToken ct = default)          => throw new NotSupportedException();
    public Task<IReadOnlyList<Belief>>          GetBeliefsAsync(Guid entityId, CancellationToken ct = default)        => throw new NotSupportedException();
    public Task<ReasoningTrail?>                GetReasoningTrailAsync(Guid entityId, CancellationToken ct = default)  => throw new NotSupportedException();
    public Task<IReadOnlyList<TrajectoryPoint>> GetTrajectoryAsync(Guid entityId, CancellationToken ct = default)     => throw new NotSupportedException();
    public Task                                 ResetAsync(CancellationToken ct = default)                            => throw new NotSupportedException();
}

/// <summary>IIdentityRegistry that throws on GetAsync to simulate mid-merge failure.</summary>
internal sealed class ThrowingIdentityRegistry : IIdentityRegistry
{
    public Task<CanonicalVendor?> GetAsync(Guid vendorId, CancellationToken ct = default)
        => throw new InvalidOperationException("Simulated registry read failure.");

    public Task SaveAsync(CanonicalVendor vendor, CancellationToken ct = default, Guid? programRunId = null) => throw new NotSupportedException();
    public Task<IReadOnlyList<CanonicalVendor>> GetAllAsync(CancellationToken ct = default)                  => throw new NotSupportedException();
    public Task MarkAbsorbedAsync(Guid vendorId, Guid survivorVendorId, CancellationToken ct = default)      => throw new NotSupportedException();
}

/// <summary>Entity store that throws on AppendBeliefAsync to simulate write failure.</summary>
internal sealed class ThrowingEntityStore : IEntityStore
{
    public Task AppendBeliefAsync(Belief belief, CancellationToken ct = default)
        => throw new InvalidOperationException("Simulated write failure.");

    public Task<IReadOnlyList<Belief>> GetCurrentBeliefsAsync(Guid entityId, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<Belief>>(Array.Empty<Belief>());

    public Task<IReadOnlyList<Belief>> GetBeliefHistoryAsync(Guid entityId, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<Belief>>(Array.Empty<Belief>());

    public Task SaveIndexAsync(EntityIndex index, CancellationToken ct = default)                                       => throw new NotSupportedException();
    public Task<EntityIndex?> GetIndexAsync(Guid entityId, CancellationToken ct = default)                             => throw new NotSupportedException();
    public Task<IReadOnlyList<EntityIndex>> GetIndexHistoryAsync(Guid entityId, CancellationToken ct = default)        => throw new NotSupportedException();
    public Task AppendPostureAsync(PostureAssignment posture, CancellationToken ct = default)                           => throw new NotSupportedException();
    public Task<PostureAssignment?> GetCurrentPostureAsync(Guid entityId, CancellationToken ct = default)              => throw new NotSupportedException();
    public Task AppendSignalAsync(Signal signal, CancellationToken ct = default)                                       => throw new NotSupportedException();
    public Task<Signal?> GetSignalAsync(Guid signalId, CancellationToken ct = default)                                => throw new NotSupportedException();
    public Task<IReadOnlyList<PostureAssignment>> GetPostureHistoryAsync(Guid entityId, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<IReadOnlyList<Signal>> GetSignalsForEntityAsync(Guid entityId, CancellationToken ct = default)         => throw new NotSupportedException();
    public Task AppendEvidenceAsync(Evidence evidence, CancellationToken ct = default)                                 => throw new NotSupportedException();
    public Task<Evidence?> GetEvidenceAsync(Guid evidenceId, CancellationToken ct = default)                          => throw new NotSupportedException();
    public Task<IReadOnlyList<Evidence>> GetEvidenceForVendorAsync(Guid vendorId, CancellationToken ct = default)     => throw new NotSupportedException();
    public Task ResetAsync(CancellationToken ct = default)                                                             => throw new NotSupportedException();
}
