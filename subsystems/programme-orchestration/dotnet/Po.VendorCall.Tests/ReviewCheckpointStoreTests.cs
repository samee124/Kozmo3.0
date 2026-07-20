using Po.VendorCall;
using Xunit;

namespace Po.VendorCall.Tests;

public sealed class ReviewCheckpointStoreTests
{
    private static readonly Guid VendorA = Guid.Parse("AAAAAAAA-0000-0000-0000-000000000001");
    private static readonly Guid VendorB = Guid.Parse("BBBBBBBB-0000-0000-0000-000000000001");

    private static readonly DateTimeOffset T0 = new(2026, 7, 1, 0, 0, 0, TimeSpan.Zero);

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static ReviewCheckpoint MakeCheckpoint(
        Guid?            vendorId   = null,
        DateTimeOffset?  createdAt  = null,
        CheckpointKind   kind       = CheckpointKind.PreMeeting,
        ReviewStatus     status     = ReviewStatus.Amber,
        ReviewMovement   movement   = ReviewMovement.Stable,
        ReviewConfidence confidence = ReviewConfidence.Medium,
        string?          q1         = null,
        IReadOnlyList<string>? sourceIds = null) =>
        new ReviewCheckpoint(
            Id:                     Guid.NewGuid(),
            VendorId:               vendorId ?? VendorA,
            VendorCallRunId:        null,
            Kind:                   kind,
            CreatedAtUtc:           createdAt ?? T0,
            Status:                 status,
            Movement:               movement,
            Confidence:             confidence,
            Q1Answer:               q1 ?? "Q1 answer",
            Q2Answer:               "Q2 answer",
            Q3Answer:               "Q3 answer",
            Q4Answer:               "Q4 answer",
            Q5Answer:               "Q5 answer",
            OpenCommitmentCount:    2,
            OverdueCommitmentCount: 1,
            UnresolvedSignalCount:  1,
            SourceReferenceIds:     sourceIds ?? []);

    private static IReviewCheckpointStore InMemory() =>
        new InMemoryReviewCheckpointStore();

    private static IReviewCheckpointStore Sqlite()
    {
        var cs = $"Data Source=:memory:";
        return new SqliteReviewCheckpointStore(cs);
    }

    // ── GetLatestAsync — no checkpoints ──────────────────────────────────────

    [Fact]
    public async Task GetLatest_NoPrior_InMemory_ReturnsNull()
        => Assert.Null(await InMemory().GetLatestAsync(VendorA, CancellationToken.None));

    [Fact]
    public async Task GetLatest_NoPrior_Sqlite_ReturnsNull()
        => Assert.Null(await Sqlite().GetLatestAsync(VendorA, CancellationToken.None));

    // ── Save then GetLatestAsync round-trip ───────────────────────────────────

    [Fact]
    public async Task RoundTrip_AllFields_InMemory()
        => await AssertRoundTrip(InMemory());

    [Fact]
    public async Task RoundTrip_AllFields_Sqlite()
        => await AssertRoundTrip(Sqlite());

    private static async Task AssertRoundTrip(IReviewCheckpointStore store)
    {
        var cp = MakeCheckpoint(
            sourceIds:  ["src-001", "src-002"],
            q1:         "Quarterly review to assess renewal readiness.",
            kind:       CheckpointKind.PostMeeting,
            status:     ReviewStatus.Red,
            movement:   ReviewMovement.Weakening,
            confidence: ReviewConfidence.Low);

        await store.SaveAsync(cp, CancellationToken.None);
        var retrieved = await store.GetLatestAsync(VendorA, CancellationToken.None);

        Assert.NotNull(retrieved);
        Assert.Equal(cp.Id,                     retrieved.Id);
        Assert.Equal(cp.VendorId,               retrieved.VendorId);
        Assert.Equal(cp.Kind,                   retrieved.Kind);
        Assert.Equal(cp.Status,                 retrieved.Status);
        Assert.Equal(cp.Movement,               retrieved.Movement);
        Assert.Equal(cp.Confidence,             retrieved.Confidence);
        Assert.Equal(cp.Q1Answer,               retrieved.Q1Answer);
        Assert.Equal(cp.Q2Answer,               retrieved.Q2Answer);
        Assert.Equal(cp.OpenCommitmentCount,    retrieved.OpenCommitmentCount);
        Assert.Equal(cp.OverdueCommitmentCount, retrieved.OverdueCommitmentCount);
        Assert.Equal(cp.SourceReferenceIds,     retrieved.SourceReferenceIds);
    }

    // ── GetLatestAsync returns most recent of multiple ────────────────────────

    [Fact]
    public async Task GetLatest_MultipleCheckpoints_ReturnsMostRecent_InMemory()
        => await AssertGetLatestMostRecent(InMemory());

    [Fact]
    public async Task GetLatest_MultipleCheckpoints_ReturnsMostRecent_Sqlite()
        => await AssertGetLatestMostRecent(Sqlite());

    private static async Task AssertGetLatestMostRecent(IReviewCheckpointStore store)
    {
        var older  = MakeCheckpoint(createdAt: T0.AddDays(-5), q1: "older");
        var newest = MakeCheckpoint(createdAt: T0.AddDays(2),  q1: "newest");
        var middle = MakeCheckpoint(createdAt: T0,             q1: "middle");

        await store.SaveAsync(older,  CancellationToken.None);
        await store.SaveAsync(middle, CancellationToken.None);
        await store.SaveAsync(newest, CancellationToken.None);

        var result = await store.GetLatestAsync(VendorA, CancellationToken.None);
        Assert.Equal("newest", result!.Q1Answer);
    }

    // ── GetHistoryAsync — descending order + maxCount ─────────────────────────

    [Fact]
    public async Task GetHistory_DescendingOrder_RespectsMax_InMemory()
        => await AssertGetHistory(InMemory());

    [Fact]
    public async Task GetHistory_DescendingOrder_RespectsMax_Sqlite()
        => await AssertGetHistory(Sqlite());

    private static async Task AssertGetHistory(IReviewCheckpointStore store)
    {
        for (var i = 1; i <= 5; i++)
            await store.SaveAsync(
                MakeCheckpoint(createdAt: T0.AddDays(i), q1: $"answer-{i}"),
                CancellationToken.None);

        var history = await store.GetHistoryAsync(VendorA, 3, CancellationToken.None);

        Assert.Equal(3, history.Count);
        Assert.Equal("answer-5", history[0].Q1Answer);
        Assert.Equal("answer-4", history[1].Q1Answer);
        Assert.Equal("answer-3", history[2].Q1Answer);
    }

    // ── Vendor isolation ──────────────────────────────────────────────────────

    [Fact]
    public async Task GetLatest_VendorIsolation_InMemory()
        => await AssertVendorIsolation(InMemory());

    [Fact]
    public async Task GetLatest_VendorIsolation_Sqlite()
        => await AssertVendorIsolation(Sqlite());

    private static async Task AssertVendorIsolation(IReviewCheckpointStore store)
    {
        await store.SaveAsync(MakeCheckpoint(vendorId: VendorA, q1: "A"), CancellationToken.None);
        await store.SaveAsync(MakeCheckpoint(vendorId: VendorB, q1: "B"), CancellationToken.None);

        var resultA = await store.GetLatestAsync(VendorA, CancellationToken.None);
        var resultB = await store.GetLatestAsync(VendorB, CancellationToken.None);

        Assert.Equal("A", resultA!.Q1Answer);
        Assert.Equal("B", resultB!.Q1Answer);
    }

    // ── SourceReferenceIds — empty list and populated list ────────────────────

    [Fact]
    public async Task SourceReferenceIds_EmptyList_RoundTrips_InMemory()
        => await AssertSourceIds(InMemory(), []);

    [Fact]
    public async Task SourceReferenceIds_EmptyList_RoundTrips_Sqlite()
        => await AssertSourceIds(Sqlite(), []);

    [Fact]
    public async Task SourceReferenceIds_PopulatedList_RoundTrips_InMemory()
        => await AssertSourceIds(InMemory(), ["a1b2", "c3d4", "e5f6"]);

    [Fact]
    public async Task SourceReferenceIds_PopulatedList_RoundTrips_Sqlite()
        => await AssertSourceIds(Sqlite(), ["a1b2", "c3d4", "e5f6"]);

    private static async Task AssertSourceIds(
        IReviewCheckpointStore store, IReadOnlyList<string> ids)
    {
        await store.SaveAsync(MakeCheckpoint(sourceIds: ids), CancellationToken.None);
        var result = await store.GetLatestAsync(VendorA, CancellationToken.None);
        Assert.Equal(ids, result!.SourceReferenceIds);
    }
}
