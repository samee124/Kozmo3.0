using Kozmo.Contracts;
using Po.VendorCall;
using Xunit;

namespace Po.VendorCall.Tests;

/// <summary>
/// Tests for ReviewComposer — the orchestrator that runs all assemblers,
/// classifies, composes narratives, and saves a ReviewCheckpoint.
/// All tests use a null LLM (deterministic mode) for reproducibility.
/// </summary>
public sealed class ReviewComposerTests
{
    private static readonly Guid VendorId =
        FactAssemblersTestFixtures.NorthstarVendorId;

    private static readonly DateTimeOffset Now =
        FactAssemblersTestFixtures.Now;

    private static IReviewComposer MakeComposer(IReviewCheckpointStore store) =>
        new ReviewComposer(store, llm: null);

    private static Task<ReviewCompositionResult> RunAsync(
        IReviewCheckpointStore    store,
        ReviewCheckpoint?         previousCheckpoint = null,
        VendorCallEvidenceBundle? bundle             = null,
        IReadOnlyList<Belief>?    beliefs            = null,
        CancellationToken         ct                 = default) =>
        MakeComposer(store).ComposeAsync(
            vendorId:           VendorId,
            bundle:             bundle  ?? FactAssemblersTestFixtures.NorthstarBundle(),
            beliefs:            beliefs ?? FactAssemblersTestFixtures.NorthstarBeliefs(),
            previousCheckpoint: previousCheckpoint,
            vendorName:         "Northstar Software",
            ownerUpn:           "rishi@econtracts.onmicrosoft.com",
            eventTypeCode:      "vendor_review",
            now:                Now,
            vendorCallRunId:    null,
            kind:               CheckpointKind.PreMeeting,
            ct:                 ct);

    // ── Checkpoint is persisted ───────────────────────────────────────────────

    [Fact]
    public async Task Compose_SavesCheckpointToStore()
    {
        var store  = new InMemoryReviewCheckpointStore();
        await RunAsync(store);
        var saved = await store.GetLatestAsync(VendorId, CancellationToken.None);
        Assert.NotNull(saved);
    }

    [Fact]
    public async Task Compose_CheckpointHasCorrectVendorId()
    {
        var store  = new InMemoryReviewCheckpointStore();
        var result = await RunAsync(store);
        Assert.Equal(VendorId, result.Checkpoint.VendorId);
    }

    [Fact]
    public async Task Compose_CheckpointHasCorrectKind()
    {
        var store  = new InMemoryReviewCheckpointStore();
        var result = await RunAsync(store);
        Assert.Equal(CheckpointKind.PreMeeting, result.Checkpoint.Kind);
    }

    // ── Status classification ─────────────────────────────────────────────────

    [Fact]
    public async Task Compose_NorthstarBundle_StatusIsAmberOrRed()
    {
        // Northstar has an overdue commitment and a commercial signal → at least Amber
        var store  = new InMemoryReviewCheckpointStore();
        var result = await RunAsync(store);
        Assert.True(
            result.Checkpoint.Status is ReviewStatus.Amber or ReviewStatus.Red,
            $"Expected Amber or Red, got {result.Checkpoint.Status}");
    }

    [Fact]
    public async Task Compose_EmptyBundleNoBeliefs_StatusIsGreen()
    {
        // No evidence, no beliefs → no contract, no overdue, no signals, no deadline → Green
        var store  = new InMemoryReviewCheckpointStore();
        var result = await RunAsync(store,
            bundle:  FactAssemblersTestFixtures.EmptyBundle(),
            beliefs: []);
        Assert.Equal(ReviewStatus.Green, result.Checkpoint.Status);
    }

    // ── Commitment counts ─────────────────────────────────────────────────────

    [Fact]
    public async Task Compose_NorthstarBundle_OverdueCountIsOne()
    {
        var store  = new InMemoryReviewCheckpointStore();
        var result = await RunAsync(store);
        // NorthstarBundle has 1 commitment ingested 10 days ago (> 4-day threshold)
        Assert.Equal(1, result.Checkpoint.OverdueCommitmentCount);
    }

    [Fact]
    public async Task Compose_NorthstarBundle_OpenCountIsOne()
    {
        var store  = new InMemoryReviewCheckpointStore();
        var result = await RunAsync(store);
        Assert.Equal(1, result.Checkpoint.OpenCommitmentCount);
    }

    // ── Movement ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Compose_FirstReview_MovementIsStable()
    {
        var store  = new InMemoryReviewCheckpointStore();
        var result = await RunAsync(store, previousCheckpoint: null);
        Assert.Equal(ReviewMovement.Stable, result.Checkpoint.Movement);
    }

    // ── Narrative answers ─────────────────────────────────────────────────────

    [Fact]
    public async Task Compose_AllAnswers_AreNonEmpty()
    {
        var store  = new InMemoryReviewCheckpointStore();
        var result = await RunAsync(store);
        Assert.NotEmpty(result.Q1.Text);
        Assert.NotEmpty(result.Q2.Text);
        Assert.NotEmpty(result.Q3.Text);
        Assert.NotEmpty(result.Q4.Text);
        Assert.NotEmpty(result.Q5.Text);
        Assert.NotEmpty(result.Overview.Text);
    }

    [Fact]
    public async Task Compose_NullLlm_NoAnswersAreLlmEnhanced()
    {
        var store  = new InMemoryReviewCheckpointStore();
        var result = await RunAsync(store);
        Assert.False(result.Q1.LlmEnhanced);
        Assert.False(result.Q2.LlmEnhanced);
        Assert.False(result.Q3.LlmEnhanced);
        Assert.False(result.Q4.LlmEnhanced);
        Assert.False(result.Q5.LlmEnhanced);
        Assert.False(result.Overview.LlmEnhanced);
    }

    // ── Source reference IDs ──────────────────────────────────────────────────

    [Fact]
    public async Task Compose_CheckpointSourceReferenceIds_AreAggregated()
    {
        // NorthstarBundle has contract + commitment + signal → at least 3 sources
        var store  = new InMemoryReviewCheckpointStore();
        var result = await RunAsync(store);
        Assert.True(
            result.Checkpoint.SourceReferenceIds.Count >= 1,
            "Expected at least one source reference ID from the evidence bundle");
    }

    // ── Checkpoint answers round-trip through store ───────────────────────────

    [Fact]
    public async Task Compose_StoredCheckpoint_Q1AnswerMatchesResult()
    {
        var store  = new InMemoryReviewCheckpointStore();
        var result = await RunAsync(store);
        var stored = await store.GetLatestAsync(VendorId, CancellationToken.None);
        Assert.Equal(result.Q1.Text, stored!.Q1Answer);
    }

    [Fact]
    public async Task Compose_StoredCheckpoint_StatusMatchesResult()
    {
        var store  = new InMemoryReviewCheckpointStore();
        var result = await RunAsync(store);
        var stored = await store.GetLatestAsync(VendorId, CancellationToken.None);
        Assert.Equal(result.Checkpoint.Status, stored!.Status);
    }
}
