using Po.VendorCall;
using Wc.Contracts;
using Xunit;

namespace Po.VendorCall.Tests;

public sealed class IdempotencyServiceTests
{
    private static readonly Guid VendorId = Guid.Parse("dd000001-0000-0000-0000-000000000001");
    private const string EventId = "evt-northstar-001";

    // ── No open check-ins ─────────────────────────────────────────────────────

    [Fact]
    public async Task HasExisting_NoOpenCheckIns_ReturnsFalse()
    {
        var store = new InMemoryCheckInStore();
        var result = await VendorCallIdempotencyService.HasExistingPreMeetingCheckInAsync(
            VendorId, EventId, store);

        Assert.False(result);
    }

    // ── Matching open check-in ────────────────────────────────────────────────

    [Fact]
    public async Task HasExisting_OpenCheckInWithMatchingTargetField_ReturnsTrue()
    {
        var store = new InMemoryCheckInStore();
        var now   = DateTimeOffset.UtcNow;

        await store.SaveAsync(new CheckIn(
            CheckInId:     Guid.NewGuid(),
            VendorId:      VendorId,
            ProgramRunId:  Guid.NewGuid(),
            Kind:          CheckInKind.DIMENSION_GAP,
            Question:      "Any changes to expect?",
            ResponseShape: ResponseShape.STATUS_SELECT,
            TargetField:   VendorCallIdempotencyService.TargetFieldFor(EventId),
            Owner:         "owner@example.com",
            Status:        PendingStatus.OPEN,
            RaisedAt:      now,
            AnsweredAt:    null,
            ExpiresAt:     now.AddDays(14),
            ResponseValue: null));

        var result = await VendorCallIdempotencyService.HasExistingPreMeetingCheckInAsync(
            VendorId, EventId, store);

        Assert.True(result);
    }

    // ── Different TargetField ─────────────────────────────────────────────────

    [Fact]
    public async Task HasExisting_OpenCheckInWithDifferentTargetField_ReturnsFalse()
    {
        var store = new InMemoryCheckInStore();
        var now   = DateTimeOffset.UtcNow;

        // Same vendor, same kind — but different event
        await store.SaveAsync(new CheckIn(
            CheckInId:     Guid.NewGuid(),
            VendorId:      VendorId,
            ProgramRunId:  Guid.NewGuid(),
            Kind:          CheckInKind.DIMENSION_GAP,
            Question:      "Any changes?",
            ResponseShape: ResponseShape.STATUS_SELECT,
            TargetField:   VendorCallIdempotencyService.TargetFieldFor("evt-different-event"),
            Owner:         "owner@example.com",
            Status:        PendingStatus.OPEN,
            RaisedAt:      now,
            AnsweredAt:    null,
            ExpiresAt:     now.AddDays(14),
            ResponseValue: null));

        var result = await VendorCallIdempotencyService.HasExistingPreMeetingCheckInAsync(
            VendorId, EventId, store);

        Assert.False(result);
    }

    // ── Different vendor ──────────────────────────────────────────────────────

    [Fact]
    public async Task HasExisting_OpenCheckInForDifferentVendor_ReturnsFalse()
    {
        var store = new InMemoryCheckInStore();
        var now   = DateTimeOffset.UtcNow;

        await store.SaveAsync(new CheckIn(
            CheckInId:     Guid.NewGuid(),
            VendorId:      Guid.NewGuid(),   // different vendor
            ProgramRunId:  Guid.NewGuid(),
            Kind:          CheckInKind.DIMENSION_GAP,
            Question:      "Any changes?",
            ResponseShape: ResponseShape.STATUS_SELECT,
            TargetField:   VendorCallIdempotencyService.TargetFieldFor(EventId),
            Owner:         "owner@example.com",
            Status:        PendingStatus.OPEN,
            RaisedAt:      now,
            AnsweredAt:    null,
            ExpiresAt:     now.AddDays(14),
            ResponseValue: null));

        var result = await VendorCallIdempotencyService.HasExistingPreMeetingCheckInAsync(
            VendorId, EventId, store);

        Assert.False(result);
    }

    // ── TargetField format ────────────────────────────────────────────────────

    [Fact]
    public void TargetFieldFor_HasExpectedFormat()
    {
        var field = VendorCallIdempotencyService.TargetFieldFor("evt-abc-123");
        Assert.Equal("vendorcall_pre:evt-abc-123", field);
    }
}
