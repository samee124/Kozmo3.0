using Ii.Completeness;
using Kozmo.Contracts;
using Wc.Contracts;
using Xunit;

namespace Ii.Completeness.Tests;

using CheckIn = global::Wc.Contracts.CheckIn;

/// <summary>
/// Tests for GapCheckInStage termination properties (Phase 5, Commit 3).
/// Termination (a): answered questions never appear — enforced by CompletenessRubric upstream.
/// Termination (b): permanentGapIds are skipped (questions that survived two cycles).
/// Termination (c): depth ladder enforced by QuestionSelector upstream.
/// </summary>
public sealed class GapCheckInStageTests
{
    private static readonly DateTimeOffset AnchorNow =
        new(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly Guid VendorA = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");

    private static IReadOnlyList<Question> L1Questions =>
        QuestionSelector.Select(SaasQuestionBank.Category, DepthLevel.L1);

    // ── AnswerType → ResponseShape mapping ─────────────────────────────────────

    [Fact]
    public async Task YesNo_question_raises_YES_NO_check_in()
    {
        var store = new InMemoryCheckInStore();
        var stage = new GapCheckInStage();
        var q     = L1Questions.First(q => q.AnswerType == AnswerType.YesNo);

        await stage.RaiseAsync(VendorA, [q.Id], L1Questions, EmptySet,
            store, "owner@test", Guid.NewGuid(), AnchorNow);

        var open = await store.GetOpenAsync();
        Assert.Single(open);
        Assert.Equal(ResponseShape.YES_NO, open[0].ResponseShape);
    }

    [Fact]
    public async Task TypedValue_question_raises_TYPED_VALUE_check_in()
    {
        var store = new InMemoryCheckInStore();
        var stage = new GapCheckInStage();
        var q     = L1Questions.First(q => q.AnswerType == AnswerType.TypedValue);

        await stage.RaiseAsync(VendorA, [q.Id], L1Questions, EmptySet,
            store, "owner@test", Guid.NewGuid(), AnchorNow);

        var open = await store.GetOpenAsync();
        Assert.Single(open);
        Assert.Equal(ResponseShape.TYPED_VALUE, open[0].ResponseShape);
    }

    [Fact]
    public async Task StatusSelect_question_raises_STATUS_SELECT_check_in()
    {
        // saas.exp.l3.2 is the only StatusSelect question in the bank.
        var allQ = QuestionSelector.Select(SaasQuestionBank.Category, DepthLevel.L3);
        var q    = allQ.First(q => q.AnswerType == AnswerType.StatusSelect);

        var store = new InMemoryCheckInStore();
        var stage = new GapCheckInStage();

        await stage.RaiseAsync(VendorA, [q.Id], allQ, EmptySet,
            store, "owner@test", Guid.NewGuid(), AnchorNow);

        var open = await store.GetOpenAsync();
        Assert.Single(open);
        Assert.Equal(ResponseShape.STATUS_SELECT, open[0].ResponseShape);
    }

    // ── Check-in field values ───────────────────────────────────────────────────

    [Fact]
    public async Task Raised_check_in_has_question_id_in_TargetField()
    {
        var store = new InMemoryCheckInStore();
        var stage = new GapCheckInStage();
        var q     = L1Questions.First(q => q.Dimension == Dimension.Operational);

        await stage.RaiseAsync(VendorA, [q.Id], L1Questions, EmptySet,
            store, "owner@test", Guid.NewGuid(), AnchorNow);

        var open = await store.GetOpenAsync();
        var ci   = Assert.Single(open);
        Assert.Equal(q.Id, ci.TargetField);
    }

    [Fact]
    public async Task Raised_check_in_has_question_text_as_Question_field()
    {
        var store = new InMemoryCheckInStore();
        var stage = new GapCheckInStage();
        var q     = L1Questions.First(q => q.Dimension == Dimension.Operational);

        await stage.RaiseAsync(VendorA, [q.Id], L1Questions, EmptySet,
            store, "owner@test", Guid.NewGuid(), AnchorNow);

        var ci = Assert.Single(await store.GetOpenAsync());
        Assert.Equal(q.Text, ci.Question);
    }

    [Fact]
    public async Task Raised_check_in_is_OPEN_DIMENSION_GAP_for_correct_vendor()
    {
        var store  = new InMemoryCheckInStore();
        var stage  = new GapCheckInStage();
        var q      = L1Questions.First();
        var runId  = Guid.NewGuid();

        await stage.RaiseAsync(VendorA, [q.Id], L1Questions, EmptySet,
            store, "owner@test", runId, AnchorNow);

        var ci = Assert.Single(await store.GetOpenAsync());
        Assert.Equal(CheckInKind.DIMENSION_GAP, ci.Kind);
        Assert.Equal(PendingStatus.OPEN,         ci.Status);
        Assert.Equal(VendorA,                    ci.VendorId);
        Assert.Equal(AnchorNow,                  ci.RaisedAt);
    }

    // ── Termination (b): permanentGapIds are skipped ────────────────────────────

    [Fact]
    public async Task Permanent_gap_question_is_not_raised()
    {
        var store   = new InMemoryCheckInStore();
        var stage   = new GapCheckInStage();
        var q       = L1Questions.First();
        var perm    = new HashSet<string>(StringComparer.Ordinal) { q.Id };

        var raised = await stage.RaiseAsync(VendorA, [q.Id], L1Questions, perm,
            store, "owner@test", Guid.NewGuid(), AnchorNow);

        Assert.Empty(raised);
        Assert.Empty(await store.GetOpenAsync());
    }

    [Fact]
    public async Task Non_permanent_gaps_are_raised_while_permanent_ones_are_skipped()
    {
        var store = new InMemoryCheckInStore();
        var stage = new GapCheckInStage();
        var qs    = L1Questions.Take(3).ToList();

        // First question is permanent; the other two should be raised.
        var perm = new HashSet<string>(StringComparer.Ordinal) { qs[0].Id };

        var raised = await stage.RaiseAsync(VendorA, qs.Select(q => q.Id).ToList(), L1Questions,
            perm, store, "owner@test", Guid.NewGuid(), AnchorNow);

        Assert.Equal(2, raised.Count);
        Assert.DoesNotContain(qs[0].Id, raised.Select(c => c.TargetField));
    }

    // ── De-dup: already-OPEN check-ins are not re-raised ───────────────────────

    [Fact]
    public async Task Gap_with_existing_OPEN_check_in_is_not_re_raised()
    {
        var store = new InMemoryCheckInStore();
        var stage = new GapCheckInStage();
        var q     = L1Questions.First();

        // First raise — creates an OPEN check-in.
        await stage.RaiseAsync(VendorA, [q.Id], L1Questions, EmptySet,
            store, "owner@test", Guid.NewGuid(), AnchorNow);
        Assert.Single(await store.GetOpenAsync());

        // Second raise — same gap, OPEN check-in still there → should be de-duped.
        var secondRaised = await stage.RaiseAsync(VendorA, [q.Id], L1Questions, EmptySet,
            store, "owner@test", Guid.NewGuid(), AnchorNow);

        Assert.Empty(secondRaised);
        Assert.Single(await store.GetOpenAsync()); // only the original remains
    }

    [Fact]
    public async Task Different_vendor_OPEN_check_in_does_not_block_raise_for_first_vendor()
    {
        var store   = new InMemoryCheckInStore();
        var stage   = new GapCheckInStage();
        var q       = L1Questions.First();
        var vendorB = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");

        // Vendor B already has an OPEN check-in for the same question.
        await stage.RaiseAsync(vendorB, [q.Id], L1Questions, EmptySet,
            store, "owner@test", Guid.NewGuid(), AnchorNow);

        // Vendor A should still get its check-in raised.
        var raised = await stage.RaiseAsync(VendorA, [q.Id], L1Questions, EmptySet,
            store, "owner@test", Guid.NewGuid(), AnchorNow);

        Assert.Single(raised);
        Assert.Equal(VendorA, raised[0].VendorId);
    }

    // ── check-in ≠ action: no ledger entry ────────────────────────────────────

    [Fact]
    public async Task Multiple_gaps_each_produce_one_check_in()
    {
        var store = new InMemoryCheckInStore();
        var stage = new GapCheckInStage();
        var gaps  = L1Questions.Select(q => q.Id).ToList();

        var raised = await stage.RaiseAsync(VendorA, gaps, L1Questions, EmptySet,
            store, "owner@test", Guid.NewGuid(), AnchorNow);

        Assert.Equal(gaps.Count, raised.Count);
        Assert.Equal(gaps.Count, (await store.GetOpenAsync()).Count);
        // All are DIMENSION_GAP, none are IDENTITY_CONFIRM
        Assert.All(raised, ci => Assert.Equal(CheckInKind.DIMENSION_GAP, ci.Kind));
    }

    // ── Transport fault isolation ────────────────────────────────────────────────
    // A transport failure (auth, network, provider outage) must not stop the check-in from
    // being raised/persisted, and must not abort raising the REMAINING gaps in the same call.

    [Fact]
    public async Task All_gaps_are_raised_even_when_transport_batch_send_throws()
    {
        var store     = new InMemoryCheckInStore();
        var stage     = new GapCheckInStage();
        var gaps      = L1Questions.Select(q => q.Id).ToList();
        var transport = new AlwaysThrowingTransport();

        var raised = await stage.RaiseAsync(VendorA, gaps, L1Questions, EmptySet,
            store, "owner@test", Guid.NewGuid(), AnchorNow, transport: transport);

        // All check-ins are persisted regardless of transport failure.
        Assert.Equal(gaps.Count, raised.Count);
        Assert.Equal(gaps.Count, (await store.GetOpenAsync()).Count);
        // One batch call (not one per check-in) — the transport throws once for the whole batch.
        Assert.Equal(1, transport.AttemptedSends);
    }

    [Fact]
    public async Task Transport_send_is_attempted_once_per_raise_call()
    {
        var store     = new InMemoryCheckInStore();
        var stage     = new GapCheckInStage();
        var q         = L1Questions.First();
        var transport = new AlwaysThrowingTransport();

        await stage.RaiseAsync(VendorA, [q.Id], L1Questions, EmptySet,
            store, "owner@test", Guid.NewGuid(), AnchorNow, transport: transport);

        Assert.Equal(1, transport.AttemptedSends);
    }

    [Fact]
    public async Task Three_gaps_produce_one_transport_call_with_batch_of_three()
    {
        var store     = new InMemoryCheckInStore();
        var stage     = new GapCheckInStage();
        var gaps      = L1Questions.Take(3).Select(q => q.Id).ToList();
        var transport = new CapturingTransport();

        await stage.RaiseAsync(VendorA, gaps, L1Questions, EmptySet,
            store, "owner@test", Guid.NewGuid(), AnchorNow, transport: transport);

        Assert.Equal(1, transport.CallCount);
        Assert.Equal(3, transport.LastBatch!.Count);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────

    private static readonly IReadOnlySet<string> EmptySet =
        new HashSet<string>(StringComparer.Ordinal);
}

// ── Fake transport that always fails, to prove send failures don't block raising ────
file sealed class AlwaysThrowingTransport : ICheckInTransport
{
    public int AttemptedSends { get; private set; }

    public Task SendAsync(IReadOnlyList<CheckIn> checkIns, CancellationToken ct = default)
    {
        AttemptedSends++;
        throw new InvalidOperationException("Simulated transport failure.");
    }
}

// ── Fake transport that captures the batch, to prove one-call-per-raise batching ──
file sealed class CapturingTransport : ICheckInTransport
{
    public int CallCount { get; private set; }
    public IReadOnlyList<CheckIn>? LastBatch { get; private set; }

    public Task SendAsync(IReadOnlyList<CheckIn> checkIns, CancellationToken ct = default)
    {
        CallCount++;
        LastBatch = checkIns;
        return Task.CompletedTask;
    }
}

// ── Minimal in-memory ICheckInStore for unit tests ──────────────────────────────

file sealed class InMemoryCheckInStore : ICheckInStore
{
    private readonly List<CheckIn> _store = [];

    public Task SaveAsync(CheckIn checkIn, CancellationToken ct = default)
    {
        // Replace if same ID, append otherwise.
        var idx = _store.FindIndex(c => c.CheckInId == checkIn.CheckInId);
        if (idx >= 0) _store[idx] = checkIn;
        else          _store.Add(checkIn);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<CheckIn>> GetOpenAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<CheckIn>>(
            _store.Where(c => c.Status == PendingStatus.OPEN).ToList().AsReadOnly());

    public Task<CheckIn?> GetAsync(Guid checkInId, CancellationToken ct = default) =>
        Task.FromResult(_store.FirstOrDefault(c => c.CheckInId == checkInId));

    public Task<IReadOnlyList<CheckIn>> GetResolvedForVendorAsync(Guid vendorId, CancellationToken ct = default)
    {
        IReadOnlyList<CheckIn> result = _store
            .Where(c => c.VendorId == vendorId &&
                        (c.Status == PendingStatus.PROCESSED || c.Status == PendingStatus.EXPIRED))
            .OrderBy(c => c.RaisedAt)
            .ToList();
        return Task.FromResult(result);
    }
}
