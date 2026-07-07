using Ig.Contracts;
using Wc.CheckIn;
using Wc.Contracts;
using Xunit;

namespace Wc.Tests;

using CheckIn = global::Wc.Contracts.CheckIn;

/// <summary>
/// Verifies the raise_checkins stage for all three real cases.
/// Uses InMemoryCheckInStore so the test has no SQLite dependency.
/// </summary>
public sealed class RaiseCheckInsStageTests
{
    // ── Shared test data ─────────────────────────────────────────────────────

    private static readonly Guid AbcId1    = new("AAAAAAAA-0000-0000-0000-000000000001");
    private static readonly Guid AbcId2    = new("AAAAAAAA-0000-0000-0000-000000000002");
    private static readonly Guid RegulusId  = new("BBBBBBBB-0000-0000-0000-000000000001");
    private static readonly Guid AequitasId = new("CCCCCCCC-0000-0000-0000-000000000001");
    private static readonly Guid RunId      = new("DDDDDDDD-0000-0000-0000-000000000001");
    private static readonly DateTimeOffset Now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private const string AbcQuestion = "Are 'ABC Tech' and 'ABC Technologies' the same vendor?";

    private static ResolutionDisposition Triage(Guid id, string name, IReadOnlyList<string> flags, string? q)
        => new ResolutionDisposition(
            ClusterId: id, MemberCandidateIds: [], ProposedCanonicalName: name,
            ComparisonKey: name.ToLowerInvariant(), EntityType: EntityType.Company,
            Disposition: Disposition.Triage, Confidence: 0.70,
            Flags: flags, TriageReason: "Near-miss", TriageQuestion: q);

    private static ResolutionDisposition Provisional(Guid id, string name)
        => new ResolutionDisposition(
            ClusterId: id, MemberCandidateIds: [], ProposedCanonicalName: name,
            ComparisonKey: name.ToLowerInvariant(), EntityType: EntityType.Company,
            Disposition: Disposition.Provisional, Confidence: 0.80,
            Flags: [ResolutionFlags.ProvisionalVendor], TriageReason: null, TriageQuestion: null);

    // ── Test 1: ABC identity → single deduplicated IDENTITY_CONFIRM ──────────

    [Fact]
    public async Task AbcIdentity_RaisedOpen_IdentityConfirm_YesNo()
    {
        var store = new InMemoryCheckInStore();
        var stage = new RaiseCheckInsStage();
        var flags = new[] { ResolutionFlags.PossibleSameEntity, ResolutionFlags.TriageRequired };

        var dispositions = new[]
        {
            Triage(AbcId1, "ABC Tech",          flags, AbcQuestion),
            Triage(AbcId2, "ABC Technologies",   flags, AbcQuestion),
        };

        var raised = await stage.RaiseAsync(dispositions, [], store, "owner@test", RunId, Now);

        Assert.Single(raised);
        var ci = raised[0];
        Assert.Equal(CheckInKind.IDENTITY_CONFIRM, ci.Kind);
        Assert.Equal(ResponseShape.YES_NO,         ci.ResponseShape);
        Assert.Equal(PendingStatus.OPEN,           ci.Status);
        Assert.Null(ci.TargetField);
    }

    // ── Test 2: question copied verbatim ─────────────────────────────────────

    [Fact]
    public async Task AbcIdentity_QuestionCopiedVerbatim()
    {
        var store = new InMemoryCheckInStore();
        var stage = new RaiseCheckInsStage();
        var flags = new[] { ResolutionFlags.PossibleSameEntity, ResolutionFlags.TriageRequired };

        var dispositions = new[]
        {
            Triage(AbcId1, "ABC Tech",          flags, AbcQuestion),
            Triage(AbcId2, "ABC Technologies",   flags, AbcQuestion),
        };

        var raised = await stage.RaiseAsync(dispositions, [], store, "owner@test", RunId, Now);

        Assert.Equal(AbcQuestion, raised[0].Question);
    }

    // ── Test 3: Regulus gap → DIMENSION_GAP / TYPED_VALUE ───────────────────

    [Fact]
    public async Task RegulusGap_RaisedOpen_DimensionGap_TypedValue()
    {
        var store = new InMemoryCheckInStore();
        var stage = new RaiseCheckInsStage();

        var gapRequests = new[]
        {
            new VendorGapRequest(
                VendorId:      RegulusId,
                Question:      "What is the current contract reference number for Regulus Communications?",
                ResponseShape: ResponseShape.TYPED_VALUE,
                TargetField:   "contract_ref"),
        };

        var raised = await stage.RaiseAsync([], gapRequests, store, "owner@test", RunId, Now);

        Assert.Single(raised);
        var ci = raised[0];
        Assert.Equal(CheckInKind.DIMENSION_GAP,  ci.Kind);
        Assert.Equal(ResponseShape.TYPED_VALUE,  ci.ResponseShape);
        Assert.Equal("contract_ref",             ci.TargetField);
        Assert.Equal(PendingStatus.OPEN,         ci.Status);
        Assert.Equal(RegulusId,                  ci.VendorId);
    }

    // ── Test 4: Aequitas gap → DIMENSION_GAP / STATUS_SELECT ────────────────

    [Fact]
    public async Task AequitasGap_RaisedOpen_DimensionGap_StatusSelect()
    {
        var store = new InMemoryCheckInStore();
        var stage = new RaiseCheckInsStage();

        var gapRequests = new[]
        {
            new VendorGapRequest(
                VendorId:      AequitasId,
                Question:      "What is the current operational status of Aequitas Capital Management?",
                ResponseShape: ResponseShape.STATUS_SELECT,
                TargetField:   null),
        };

        var raised = await stage.RaiseAsync([], gapRequests, store, "owner@test", RunId, Now);

        Assert.Single(raised);
        Assert.Equal(CheckInKind.DIMENSION_GAP,   raised[0].Kind);
        Assert.Equal(ResponseShape.STATUS_SELECT, raised[0].ResponseShape);
        Assert.Equal(PendingStatus.OPEN,          raised[0].Status);
    }

    // ── Test 5: AutoConfirm / Provisional dispositions are NOT raised ────────

    [Fact]
    public async Task NonTriage_NotRaised()
    {
        var store = new InMemoryCheckInStore();
        var stage = new RaiseCheckInsStage();

        var dispositions = new[]
        {
            Provisional(RegulusId, "Regulus Communications"),
        };

        var raised = await stage.RaiseAsync(dispositions, [], store, "owner@test", RunId, Now);

        Assert.Empty(raised);
        Assert.Empty(await store.GetOpenAsync());
    }

    // ── Test 6: rerun dedup — DIMENSION_GAP with TargetField ─────────────────

    [Fact]
    public async Task Rerun_DimensionGap_WithTargetField_NotDuplicated()
    {
        var store = new InMemoryCheckInStore();
        var stage = new RaiseCheckInsStage();
        var gap   = new VendorGapRequest(RegulusId, "Contract ref?", ResponseShape.TYPED_VALUE, "contract_ref");

        // First run — raises the check-in.
        await stage.RaiseAsync([], [gap], store, "owner@test", RunId, Now);
        Assert.Single(await store.GetOpenAsync());

        // Second run with a new ProgramRunId — same (VendorId, TargetField) still OPEN → skip.
        var run2    = Guid.NewGuid();
        var raised2 = await stage.RaiseAsync([], [gap], store, "owner@test", run2, Now);

        Assert.Empty(raised2);
        Assert.Single(await store.GetOpenAsync()); // only the original remains
    }

    // ── Test 7: rerun dedup — IDENTITY_CONFIRM ───────────────────────────────

    [Fact]
    public async Task Rerun_IdentityConfirm_NotDuplicated()
    {
        var store = new InMemoryCheckInStore();
        var stage = new RaiseCheckInsStage();
        var flags = new[] { ResolutionFlags.PossibleSameEntity, ResolutionFlags.TriageRequired };

        var dispositions = new[]
        {
            Triage(AbcId1, "ABC Tech",        flags, AbcQuestion),
            Triage(AbcId2, "ABC Technologies", flags, AbcQuestion),
        };

        // First run — raises the identity confirm.
        await stage.RaiseAsync(dispositions, [], store, "owner@test", RunId, Now);
        Assert.Single(await store.GetOpenAsync());

        // Second run — same question still OPEN → skip.
        var run2    = Guid.NewGuid();
        var raised2 = await stage.RaiseAsync(dispositions, [], store, "owner@test", run2, Now);

        Assert.Empty(raised2);
        Assert.Single(await store.GetOpenAsync());
    }

    // ── Test 8: raised check-ins are persisted OPEN and retrievable ──────────

    [Fact]
    public async Task RaisedCheckIns_PersistedOpen_Retrievable()
    {
        var store = new InMemoryCheckInStore();
        var stage = new RaiseCheckInsStage();
        var flags = new[] { ResolutionFlags.PossibleSameEntity, ResolutionFlags.TriageRequired };

        var dispositions = new[]
        {
            Triage(AbcId1, "ABC Tech",         flags, AbcQuestion),
            Triage(AbcId2, "ABC Technologies", flags, AbcQuestion),
        };
        var gapRequests = new[]
        {
            new VendorGapRequest(RegulusId, "Contract ref?", ResponseShape.TYPED_VALUE, "contract_ref"),
        };

        await stage.RaiseAsync(dispositions, gapRequests, store, "owner@test", RunId, Now);

        var open = await store.GetOpenAsync();
        Assert.Equal(2, open.Count);
        Assert.All(open, ci => Assert.Equal(PendingStatus.OPEN, ci.Status));
    }
}

// ── In-memory ICheckInStore for tests ────────────────────────────────────────

internal sealed class InMemoryCheckInStore : ICheckInStore
{
    private readonly Dictionary<Guid, CheckIn> _store = new();

    public Task SaveAsync(CheckIn checkIn, CancellationToken ct = default)
    {
        _store[checkIn.CheckInId] = checkIn;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<CheckIn>> GetOpenAsync(CancellationToken ct = default)
    {
        IReadOnlyList<CheckIn> result = _store.Values
            .Where(c => c.Status == PendingStatus.OPEN)
            .ToList();
        return Task.FromResult(result);
    }

    public Task<CheckIn?> GetAsync(Guid checkInId, CancellationToken ct = default)
        => Task.FromResult(_store.TryGetValue(checkInId, out var c) ? c : null);

    public Task<IReadOnlyList<CheckIn>> GetResolvedForVendorAsync(Guid vendorId, CancellationToken ct = default)
    {
        IReadOnlyList<CheckIn> result = _store.Values
            .Where(c => c.VendorId == vendorId &&
                        (c.Status == PendingStatus.PROCESSED || c.Status == PendingStatus.EXPIRED))
            .OrderBy(c => c.RaisedAt)
            .ToList();
        return Task.FromResult(result);
    }
}
