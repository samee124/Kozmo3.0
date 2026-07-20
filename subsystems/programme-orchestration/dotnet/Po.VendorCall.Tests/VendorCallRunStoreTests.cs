using Po.VendorCall;
using Xunit;

namespace Po.VendorCall.Tests;

public sealed class VendorCallRunStoreTests
{
    private static readonly Guid VendorA = Guid.Parse("AAAAAAAA-0000-0000-0000-000000000001");
    private static readonly Guid VendorB = Guid.Parse("BBBBBBBB-0000-0000-0000-000000000001");

    private static VendorCallRun MakeRun(
        string          eventId  = "event-001",
        Guid?           vendorId = null,
        VendorCallStatus status  = VendorCallStatus.BriefingSent) => new()
    {
        Id                      = Guid.NewGuid(),
        EventId                 = eventId,
        VendorId                = vendorId ?? VendorA,
        VendorName              = "Test Vendor",
        MeetingSubject          = "Test Meeting",
        StartUtc                = DateTimeOffset.UtcNow,
        EndUtc                  = DateTimeOffset.UtcNow.AddHours(1),
        SignedInUserPrincipalId = "user@test.com",
        Status                  = status,
        CreatedAt               = DateTimeOffset.UtcNow,
        UpdatedAt               = DateTimeOffset.UtcNow,
    };

    // ── GetByIdAsync ──────────────────────────────────────────────────────────

    [Fact]
    public async Task GetById_NotFound_ReturnsNull()
    {
        var store = new InMemoryVendorCallRunStore();
        var result = await store.GetByIdAsync(Guid.NewGuid(), CancellationToken.None);
        Assert.Null(result);
    }

    [Fact]
    public async Task GetById_AfterSave_ReturnsRun()
    {
        var store = new InMemoryVendorCallRunStore();
        var run   = MakeRun(eventId: "ev-xyz");

        await store.SaveAsync(run, CancellationToken.None);
        var result = await store.GetByIdAsync(run.Id, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(run.Id, result.Id);
    }

    // ── GetByEventIdAsync ─────────────────────────────────────────────────────

    [Fact]
    public async Task GetByEventId_NotFound_ReturnsNull()
    {
        var store = new InMemoryVendorCallRunStore();
        var result = await store.GetByEventIdAsync("no-such-event", CancellationToken.None);
        Assert.Null(result);
    }

    [Fact]
    public async Task SaveThenGetByEventId_ReturnsSavedRun()
    {
        var store = new InMemoryVendorCallRunStore();
        var run   = MakeRun(eventId: "event-abc");

        await store.SaveAsync(run, CancellationToken.None);
        var result = await store.GetByEventIdAsync("event-abc", CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(run.Id, result.Id);
        Assert.Equal("event-abc", result.EventId);
    }

    // ── SaveAsync upsert ──────────────────────────────────────────────────────

    [Fact]
    public async Task SaveAsync_SameEventId_Upserts()
    {
        var store = new InMemoryVendorCallRunStore();
        var run   = MakeRun(eventId: "event-001", status: VendorCallStatus.BriefingSent);

        await store.SaveAsync(run, CancellationToken.None);

        run.Status = VendorCallStatus.TranscriptReady;
        await store.SaveAsync(run, CancellationToken.None);

        var result = await store.GetByEventIdAsync("event-001", CancellationToken.None);
        Assert.Equal(VendorCallStatus.TranscriptReady, result!.Status);
    }

    // ── GetByStatusAsync ──────────────────────────────────────────────────────

    [Fact]
    public async Task GetByStatus_FiltersCorrectly()
    {
        var store = new InMemoryVendorCallRunStore();
        await store.SaveAsync(MakeRun("e1", status: VendorCallStatus.BriefingSent),    CancellationToken.None);
        await store.SaveAsync(MakeRun("e2", status: VendorCallStatus.TranscriptReady), CancellationToken.None);
        await store.SaveAsync(MakeRun("e3", status: VendorCallStatus.BriefingSent),    CancellationToken.None);

        var briefed = await store.GetByStatusAsync(VendorCallStatus.BriefingSent, CancellationToken.None);
        Assert.Equal(2, briefed.Count);

        var ready = await store.GetByStatusAsync(VendorCallStatus.TranscriptReady, CancellationToken.None);
        Assert.Single(ready);
    }

    // ── GetByVendorIdAsync ────────────────────────────────────────────────────

    [Fact]
    public async Task GetByVendorId_FiltersCorrectly()
    {
        var store = new InMemoryVendorCallRunStore();
        await store.SaveAsync(MakeRun("e1", vendorId: VendorA), CancellationToken.None);
        await store.SaveAsync(MakeRun("e2", vendorId: VendorB), CancellationToken.None);
        await store.SaveAsync(MakeRun("e3", vendorId: VendorA), CancellationToken.None);

        var runsA = await store.GetByVendorIdAsync(VendorA, CancellationToken.None);
        Assert.Equal(2, runsA.Count);
        Assert.All(runsA, r => Assert.Equal(VendorA, r.VendorId));

        var runsB = await store.GetByVendorIdAsync(VendorB, CancellationToken.None);
        Assert.Single(runsB);
    }
}
