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

        await transport.SendAsync(ci);

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
