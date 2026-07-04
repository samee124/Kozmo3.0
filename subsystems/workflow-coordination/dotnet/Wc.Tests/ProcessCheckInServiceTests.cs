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

public sealed class ProcessCheckInServiceTests
{
    private static readonly Guid AbcId1    = new("CCCCCCCC-0000-0000-0000-000000000001");
    private static readonly Guid AbcId2    = new("CCCCCCCC-0000-0000-0000-000000000002");
    private static readonly Guid RegulusId = new("DDDDDDDD-0000-0000-0000-000000000001");
    private static readonly Guid RunId     = new("EEEEEEEE-0000-0000-0000-000000000001");
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    // Minimal profile — ClaimKeyCatalogue is empty so dimension defaults to Financial
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

    // ── helpers ───────────────────────────────────────────────────────────────

    private static CheckIn IdentityCheckIn(Guid ciId, PendingStatus status, string responseValue, Guid pairedId) =>
        new(CheckInId:      ciId,
            VendorId:       AbcId1,
            ProgramRunId:   RunId,
            Kind:           CheckInKind.IDENTITY_CONFIRM,
            Question:       "Are Abc Corp and Abc Inc the same vendor?",
            ResponseShape:  ResponseShape.YES_NO,
            TargetField:    null,
            Owner:          "analyst@test",
            Status:         status,
            RaisedAt:       Now,
            AnsweredAt:     status == PendingStatus.ANSWERED ? Now.AddHours(1) : null,
            ExpiresAt:      null,
            ResponseValue:  responseValue,
            PairedVendorId: pairedId);

    private static CheckIn DimGapCheckIn(Guid ciId, Guid vendorId, PendingStatus status) =>
        new(CheckInId:      ciId,
            VendorId:       vendorId,
            ProgramRunId:   RunId,
            Kind:           CheckInKind.DIMENSION_GAP,
            Question:       "What is the SLA uptime percentage?",
            ResponseShape:  ResponseShape.TYPED_VALUE,
            TargetField:    null,
            Owner:          "analyst@test",
            Status:         status,
            RaisedAt:       Now,
            AnsweredAt:     status == PendingStatus.ANSWERED ? Now.AddHours(1) : null,
            ExpiresAt:      null,
            ResponseValue:  status == PendingStatus.ANSWERED ? "0.99" : null);

    private static CanonicalVendor TriageVendor(Guid id, string name) => new(
        VendorId:          id,
        CanonicalName:     name,
        Aliases:           new List<VendorAlias>(),
        ComparisonKey:     name.ToLowerInvariant(),
        EntityType:        EntityType.Company,
        Confidence:        0.8,
        Flags:             new List<string>(),
        Status:            RegistryStatus.Triage,
        RebrandMapRef:     null,
        AcquisitionMapRef: null,
        CreatedAt:         Now);

    private static ProcessCheckInService Svc() => new();

    // ── 1. IDENTITY_CONFIRM YES → merge ───────────────────────────────────────

    [Fact]
    public async Task CheckIn_AbcIdentity_AnswerYes_MergesToOneVendor()
    {
        var registry = new InMemoryIdentityRegistry();
        registry.Seed(TriageVendor(AbcId1, "Abc Corp"));
        registry.Seed(TriageVendor(AbcId2, "Abc Inc"));
        var checkInStore = new InMemoryCheckInStore();
        var ciId         = Guid.NewGuid();
        await checkInStore.SaveAsync(IdentityCheckIn(ciId, PendingStatus.ANSWERED, "true", AbcId2));

        var facade      = new TrackingFacade();
        var entityStore = new InMemoryEntityStore();
        var writeSvc    = new VendorFileWriteService(entityStore, EmptyProfile);

        var result = await Svc().ProcessAsync(ciId, checkInStore, registry, writeSvc, facade, EmptyProfile, Now);

        Assert.Equal(ProcessOutcome.Ok, result.Outcome);
        Assert.Equal(AbcId1, result.AffectedVendorId);

        // Survivor is Confirmed with absorbed vendor's name folded as an alias
        var survivor = await registry.GetAsync(AbcId1);
        Assert.NotNull(survivor);
        Assert.Equal(RegistryStatus.Confirmed, survivor!.Status);
        Assert.Contains(survivor.Aliases, a => a.RawName == "Abc Inc");

        // Absorbed vendor is non-destructively marked (Status=Absorbed, points to survivor)
        var absorbed = await registry.GetAsync(AbcId2);
        Assert.NotNull(absorbed);
        Assert.Equal(RegistryStatus.Absorbed, absorbed!.Status);
        Assert.Equal(AbcId1, absorbed.AbsorbedIntoVendorId);

        // Recompute called for survivor only — absorbed is excluded from active pipeline
        Assert.Contains(AbcId1, facade.RecomputedVendors);
        Assert.DoesNotContain(AbcId2, facade.RecomputedVendors);

        var stored = await checkInStore.GetAsync(ciId);
        Assert.Equal(PendingStatus.PROCESSED, stored!.Status);
    }

    // ── 2. IDENTITY_CONFIRM NO → two confirmed vendors ────────────────────────

    [Fact]
    public async Task CheckIn_AbcIdentity_AnswerNo_TwoConfirmedVendors()
    {
        var registry = new InMemoryIdentityRegistry();
        registry.Seed(TriageVendor(AbcId1, "Abc Corp"));
        registry.Seed(TriageVendor(AbcId2, "Abc Inc"));
        var checkInStore = new InMemoryCheckInStore();
        var ciId         = Guid.NewGuid();
        await checkInStore.SaveAsync(IdentityCheckIn(ciId, PendingStatus.ANSWERED, "false", AbcId2));

        await Svc().ProcessAsync(ciId, checkInStore, registry,
            new VendorFileWriteService(new InMemoryEntityStore(), EmptyProfile),
            new TrackingFacade(), EmptyProfile, Now);

        // Both vendors promoted to Confirmed; neither absorbed
        var v1 = await registry.GetAsync(AbcId1);
        var v2 = await registry.GetAsync(AbcId2);
        Assert.Equal(RegistryStatus.Confirmed, v1!.Status);
        Assert.Equal(RegistryStatus.Confirmed, v2!.Status);

        // Two distinct active entries remain
        var all = await registry.GetAllAsync();
        Assert.Equal(2, all.Count);

        var stored = await checkInStore.GetAsync(ciId);
        Assert.Equal(PendingStatus.PROCESSED, stored!.Status);
    }

    // ── 3. DIMENSION_GAP → belief written + vendor recomputed ─────────────────

    [Fact]
    public async Task CheckIn_RegulusGap_AnswerValue_AppendsBelief_RecomputesVendor()
    {
        var entityStore  = new InMemoryEntityStore();
        var checkInStore = new InMemoryCheckInStore();
        var ciId         = Guid.NewGuid();
        await checkInStore.SaveAsync(DimGapCheckIn(ciId, RegulusId, PendingStatus.ANSWERED));

        var facade = new TrackingFacade();
        var result = await Svc().ProcessAsync(ciId, checkInStore,
            new InMemoryIdentityRegistry(),
            new VendorFileWriteService(entityStore, EmptyProfile),
            facade, EmptyProfile, Now);

        Assert.Equal(ProcessOutcome.Ok, result.Outcome);
        Assert.Equal(RegulusId, result.AffectedVendorId);

        Assert.NotEmpty(entityStore.AllBeliefs);
        Assert.All(entityStore.AllBeliefs, b => Assert.Equal(RegulusId, b.EntityId));

        Assert.Contains(RegulusId, facade.RecomputedVendors);

        var stored = await checkInStore.GetAsync(ciId);
        Assert.Equal(PendingStatus.PROCESSED, stored!.Status);
    }

    // ── 4. Response becomes belief with correct tier and provenance ────────────

    [Fact]
    public async Task CheckIn_ResponseBecomesBelief_TieredAndCited()
    {
        var entityStore  = new InMemoryEntityStore();
        var checkInStore = new InMemoryCheckInStore();
        var ciId         = Guid.NewGuid();
        await checkInStore.SaveAsync(DimGapCheckIn(ciId, RegulusId, PendingStatus.ANSWERED));

        await Svc().ProcessAsync(ciId, checkInStore,
            new InMemoryIdentityRegistry(),
            new VendorFileWriteService(entityStore, EmptyProfile),
            new TrackingFacade(), EmptyProfile, Now);

        var belief = Assert.Single(entityStore.AllBeliefs);
        Assert.Equal(SourceTier.Reported, belief.SourceTier);
        Assert.NotNull(belief.Provenance);
        Assert.Equal(ciId, belief.Provenance!.EvidenceId);
    }

    // ── 5. Wrong-match guard — response applies only to its own check-in ───────
    //
    // The test seeds REAL pre-existing state for Regulus: a registry entry with a known
    // status and one existing belief. After processing AbcId1's check-in, the snapshot
    // of Regulus is compared field-by-field to prove zero mutation — not just absence.

    [Fact]
    public async Task CheckIn_WrongMatchGuard_ResponseAppliesOnlyToOwnCheckin()
    {
        // ── Arrange: Regulus has real pre-existing state ──────────────────────
        var registry = new InMemoryIdentityRegistry();
        registry.Seed(TriageVendor(AbcId1, "Abc Corp"));
        // Regulus is already Confirmed — a status that must not change
        registry.Seed(TriageVendor(RegulusId, "Regulus Analytics") with
        {
            Status = RegistryStatus.Confirmed
        });

        var entityStore = new InMemoryEntityStore();
        // Regulus already has one belief — the count and identity must survive
        var existingBelief = new Belief(
            Id:            new Guid("FFFF0000-0000-0000-0000-000000000001"),
            EntityId:      RegulusId,
            Dimension:     Dimension.Financial,
            Criterion:     "pre_existing",
            Value:         0.7,
            SourceTier:    SourceTier.Verified,
            Confidence:    0.8,
            Freshness:     1.0,
            Derivation:    "seed",
            SourceSignals: [],
            Version:       1,
            SupersededBy:  null,
            CreatedAt:     Now,
            TraceId:       Guid.NewGuid());
        await entityStore.AppendBeliefAsync(existingBelief);

        // Snapshot Regulus state BEFORE processing any check-in
        var regulusBefore         = await registry.GetAsync(RegulusId);
        var regulusBeliefsBefore  = entityStore.AllBeliefs.Where(b => b.EntityId == RegulusId).ToList();

        var checkInStore = new InMemoryCheckInStore();
        var ciIdA        = Guid.NewGuid();
        var ciIdB        = Guid.NewGuid();
        await checkInStore.SaveAsync(DimGapCheckIn(ciIdA, AbcId1, PendingStatus.ANSWERED));
        await checkInStore.SaveAsync(DimGapCheckIn(ciIdB, RegulusId, PendingStatus.OPEN));

        var facade = new TrackingFacade();

        // ── Act: process ONLY AbcId1's check-in ──────────────────────────────
        await Svc().ProcessAsync(ciIdA, checkInStore, registry,
            new VendorFileWriteService(entityStore, EmptyProfile),
            facade, EmptyProfile, Now);

        // ── Assert: Regulus is byte-for-byte identical to the snapshot ────────

        // Registry: status, absorption, alias count all unchanged
        var regulusAfter = await registry.GetAsync(RegulusId);
        Assert.Equal(regulusBefore!.Status,               regulusAfter!.Status);
        Assert.Equal(regulusBefore.AbsorbedIntoVendorId,  regulusAfter.AbsorbedIntoVendorId);
        Assert.Equal(regulusBefore.Aliases.Count,         regulusAfter.Aliases.Count);

        // Beliefs: count unchanged; the one pre-existing belief is still there,
        // identified by its fixed ID — no new beliefs were appended for Regulus
        var regulusBeliefsAfter = entityStore.AllBeliefs.Where(b => b.EntityId == RegulusId).ToList();
        Assert.Equal(regulusBeliefsBefore.Count, regulusBeliefsAfter.Count);
        Assert.Equal(existingBelief.Id, regulusBeliefsAfter.Single().Id);

        // Engine: recompute was not called for Regulus
        Assert.DoesNotContain(RegulusId, facade.RecomputedVendors);

        // Check-in: ciB is still OPEN — processing ciA did not alter a different vendor's queue
        var ciB = await checkInStore.GetAsync(ciIdB);
        Assert.Equal(PendingStatus.OPEN, ciB!.Status);
    }

    // ── 6. Unknown or closed check-in is rejected with no side effects ─────────

    [Fact]
    public async Task CheckIn_UnknownOrClosedId_Rejected()
    {
        var checkInStore = new InMemoryCheckInStore();
        var entityStore  = new InMemoryEntityStore();
        var facade       = new TrackingFacade();
        var writeSvc     = new VendorFileWriteService(entityStore, EmptyProfile);
        var registry     = new InMemoryIdentityRegistry();

        // Unknown ID → NotFound
        var r1 = await Svc().ProcessAsync(Guid.NewGuid(), checkInStore, registry, writeSvc, facade, EmptyProfile, Now);
        Assert.Equal(ProcessOutcome.NotFound, r1.Outcome);

        // Already PROCESSED → AlreadyProcessed
        var ciId = Guid.NewGuid();
        await checkInStore.SaveAsync(DimGapCheckIn(ciId, RegulusId, PendingStatus.ANSWERED) with
        {
            Status = PendingStatus.PROCESSED
        });
        var r2 = await Svc().ProcessAsync(ciId, checkInStore, registry, writeSvc, facade, EmptyProfile, Now);
        Assert.Equal(ProcessOutcome.AlreadyProcessed, r2.Outcome);

        // No side effects on any rejection
        Assert.Empty(entityStore.AllBeliefs);
        Assert.Empty(facade.RecomputedVendors);
    }

    // ── 7. Two open check-ins coexist; processing one does not block the other ─

    [Fact]
    public async Task CheckIn_PendingState_VendorRendersPending_ProgramDoesNotBlock()
    {
        var checkInStore = new InMemoryCheckInStore();
        var ciId1        = Guid.NewGuid();
        var ciId2        = Guid.NewGuid();

        // Two OPEN check-ins for two different vendors
        await checkInStore.SaveAsync(DimGapCheckIn(ciId1, AbcId1, PendingStatus.OPEN));
        await checkInStore.SaveAsync(DimGapCheckIn(ciId2, RegulusId, PendingStatus.OPEN));

        // Transition ciId1 to ANSWERED (simulates the /checkins/{id}/answer endpoint)
        await checkInStore.SaveAsync(DimGapCheckIn(ciId1, AbcId1, PendingStatus.ANSWERED));

        var result = await Svc().ProcessAsync(ciId1, checkInStore,
            new InMemoryIdentityRegistry(),
            new VendorFileWriteService(new InMemoryEntityStore(), EmptyProfile),
            new TrackingFacade(), EmptyProfile, Now);

        Assert.Equal(ProcessOutcome.Ok, result.Outcome);

        // ciId1 is PROCESSED
        var ci1 = await checkInStore.GetAsync(ciId1);
        Assert.Equal(PendingStatus.PROCESSED, ci1!.Status);

        // ciId2 is still OPEN — the program was not blocked on ci1
        var ci2 = await checkInStore.GetAsync(ciId2);
        Assert.Equal(PendingStatus.OPEN, ci2!.Status);

        // GetOpenAsync excludes PROCESSED; only ciId2 remains pending
        var open = await checkInStore.GetOpenAsync();
        Assert.Single(open);
        Assert.Equal(ciId2, open[0].CheckInId);
    }
}

// ── Test fakes ────────────────────────────────────────────────────────────────

internal sealed class InMemoryIdentityRegistry : IIdentityRegistry
{
    private readonly Dictionary<Guid, CanonicalVendor> _vendors = new();

    public void Seed(CanonicalVendor vendor) => _vendors[vendor.VendorId] = vendor;

    public Task SaveAsync(CanonicalVendor vendor, CancellationToken ct = default, Guid? programRunId = null)
    {
        _vendors[vendor.VendorId] = vendor;
        return Task.CompletedTask;
    }

    public Task<CanonicalVendor?> GetAsync(Guid vendorId, CancellationToken ct = default)
        => Task.FromResult(_vendors.GetValueOrDefault(vendorId));

    public Task<IReadOnlyList<CanonicalVendor>> GetAllAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<CanonicalVendor>>(_vendors.Values.ToList());

    public Task MarkAbsorbedAsync(Guid vendorId, Guid survivorVendorId, CancellationToken ct = default)
    {
        if (_vendors.TryGetValue(vendorId, out var v))
            _vendors[vendorId] = v with { Status = RegistryStatus.Absorbed, AbsorbedIntoVendorId = survivorVendorId };
        return Task.CompletedTask;
    }
}

internal sealed class TrackingFacade : IIiFacade
{
    public List<Guid> RecomputedVendors { get; } = new();

    public Task<VendorJudgement?> RecomputeVendorAsync(Guid entityId, CancellationToken ct = default)
    {
        RecomputedVendors.Add(entityId);
        return Task.FromResult<VendorJudgement?>(null);
    }

    public Task<Guid>                           SubmitSignalAsync(Signal signal, CancellationToken ct = default)      => throw new NotSupportedException();
    public Task<PostureAssignment?>              GetPostureAsync(Guid entityId, CancellationToken ct = default)        => throw new NotSupportedException();
    public Task<EntityIndex?>                   GetIndexAsync(Guid entityId, CancellationToken ct = default)          => throw new NotSupportedException();
    public Task<IReadOnlyList<Belief>>          GetBeliefsAsync(Guid entityId, CancellationToken ct = default)        => throw new NotSupportedException();
    public Task<ReasoningTrail?>                GetReasoningTrailAsync(Guid entityId, CancellationToken ct = default)  => throw new NotSupportedException();
    public Task<IReadOnlyList<TrajectoryPoint>> GetTrajectoryAsync(Guid entityId, CancellationToken ct = default)     => throw new NotSupportedException();
    public Task                                 ResetAsync(CancellationToken ct = default)                            => throw new NotSupportedException();
}

internal sealed class InMemoryEntityStore : IEntityStore
{
    private readonly List<Belief> _beliefs = new();
    public IReadOnlyList<Belief> AllBeliefs => _beliefs;

    public Task AppendBeliefAsync(Belief belief, CancellationToken ct = default)
    {
        _beliefs.Add(belief);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<Belief>> GetCurrentBeliefsAsync(Guid entityId, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<Belief>>(
               _beliefs.Where(b => b.EntityId == entityId && b.SupersededBy == null).ToList());

    public Task<IReadOnlyList<Belief>> GetBeliefHistoryAsync(Guid entityId, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<Belief>>(
               _beliefs.Where(b => b.EntityId == entityId).ToList());

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
