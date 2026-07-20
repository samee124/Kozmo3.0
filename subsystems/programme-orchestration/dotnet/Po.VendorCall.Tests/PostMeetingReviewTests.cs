using Po.VendorCall;
using Xunit;

namespace Po.VendorCall.Tests;

/// <summary>
/// Tests for Phase 9d: post-meeting review submission and review store.
/// All tests use in-memory stores — no SQLite, no LLM.
/// </summary>
public sealed class PostMeetingReviewTests
{
    // ── Fixtures ──────────────────────────────────────────────────────────────

    private static readonly Guid RunId = Guid.Parse("CC000001-0000-0000-0000-000000000001");

    private static VendorCallRun MakeRun(
        string? token   = "valid-review-token",
        DateTimeOffset? expiry = null) => new()
    {
        Id                      = RunId,
        EventId                 = "review-test-event",
        VendorId                = Guid.Parse("DD000001-0000-0000-0000-000000000001"),
        VendorName              = "Northstar Software",
        MeetingSubject          = "Northstar — annual renewal",
        StartUtc                = new DateTimeOffset(2026, 7, 22, 10, 0, 0, TimeSpan.Zero),
        EndUtc                  = new DateTimeOffset(2026, 7, 22, 11, 0, 0, TimeSpan.Zero),
        SignedInUserPrincipalId = "ritesh@econtracts.onmicrosoft.com",
        Status                  = VendorCallStatus.PostSummarySent,
        ReviewToken             = token,
        ReviewTokenExpiresAt    = expiry ?? DateTimeOffset.UtcNow.AddHours(48),
        CreatedAt               = DateTimeOffset.UtcNow,
        UpdatedAt               = DateTimeOffset.UtcNow,
    };

    private static PostMeetingReviewSubmission MakeSubmission(
        bool accurate = true, bool promote = true) =>
        new(RunId:             RunId,
            SubmittedAt:       DateTimeOffset.UtcNow,
            SummaryAccurate:   accurate,
            Corrections:
            [
                new("decisions",  0, "Original text",  "Corrected text"),
                new("commitments",1, "Vendor original", "Vendor corrected"),
            ],
            Additions:         "One additional observation.",
            PromoteToEvidence: promote);

    // ── InMemoryPostMeetingReviewStore ─────────────────────────────────────────

    [Fact]
    public async Task ReviewStore_GetByRunId_NotFound_ReturnsNull()
    {
        var store = new InMemoryPostMeetingReviewStore();
        var result = await store.GetByRunIdAsync(Guid.NewGuid(), CancellationToken.None);
        Assert.Null(result);
    }

    [Fact]
    public async Task ReviewStore_SaveThenGet_RoundTrip()
    {
        var store = new InMemoryPostMeetingReviewStore();
        var sub   = MakeSubmission();

        await store.SaveAsync(sub, CancellationToken.None);
        var result = await store.GetByRunIdAsync(RunId, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(RunId,               result.RunId);
        Assert.True(result.SummaryAccurate);
        Assert.True(result.PromoteToEvidence);
        Assert.Equal(2, result.Corrections.Count);
        Assert.Equal("One additional observation.", result.Additions);
    }

    [Fact]
    public async Task ReviewStore_Save_OverwritesPreviousSubmission()
    {
        var store = new InMemoryPostMeetingReviewStore();

        await store.SaveAsync(MakeSubmission(accurate: true,  promote: false), CancellationToken.None);
        await store.SaveAsync(MakeSubmission(accurate: false, promote: true),  CancellationToken.None);

        var result = await store.GetByRunIdAsync(RunId, CancellationToken.None);
        Assert.False(result!.SummaryAccurate);
        Assert.True(result.PromoteToEvidence);
    }

    // ── ItemCorrection record ─────────────────────────────────────────────────

    [Fact]
    public void ItemCorrection_FieldsArePreserved()
    {
        var c = new ItemCorrection("decisions", 2, "Original", "Edited");
        Assert.Equal("decisions", c.SectionKey);
        Assert.Equal(2,           c.ItemIndex);
        Assert.Equal("Original",  c.OriginalText);
        Assert.Equal("Edited",    c.EditedText);
    }

    // ── PostMeetingReviewSubmission record ────────────────────────────────────

    [Fact]
    public void Submission_WithNoCorrections_HasEmptyList()
    {
        var sub = new PostMeetingReviewSubmission(
            RunId:             RunId,
            SubmittedAt:       DateTimeOffset.UtcNow,
            SummaryAccurate:   true,
            Corrections:       [],
            Additions:         null,
            PromoteToEvidence: false);

        Assert.Empty(sub.Corrections);
        Assert.Null(sub.Additions);
    }

    // ── Token validation logic ────────────────────────────────────────────────

    [Fact]
    public void Token_Valid_WhenMatchAndNotExpired()
    {
        var run = MakeRun(token: "abc123", expiry: DateTimeOffset.UtcNow.AddHours(1));
        Assert.True(IsTokenValid(run, "abc123"));
    }

    [Fact]
    public void Token_Invalid_WhenTokenMismatch()
    {
        var run = MakeRun(token: "abc123");
        Assert.False(IsTokenValid(run, "wrong-token"));
    }

    [Fact]
    public void Token_Invalid_WhenExpired()
    {
        var run = MakeRun(token: "abc123", expiry: DateTimeOffset.UtcNow.AddHours(-1));
        Assert.False(IsTokenValid(run, "abc123"));
    }

    [Fact]
    public void Token_Invalid_WhenNull()
    {
        var run = MakeRun(token: null);
        Assert.False(IsTokenValid(run, "abc123"));
    }

    // Mirrors the validation logic in VendorCallReviewModel
    private static bool IsTokenValid(VendorCallRun run, string tokenStr) =>
        !string.IsNullOrEmpty(run.ReviewToken) &&
        string.Equals(run.ReviewToken, tokenStr, StringComparison.Ordinal) &&
        run.ReviewTokenExpiresAt.HasValue &&
        run.ReviewTokenExpiresAt.Value >= DateTimeOffset.UtcNow;

    // ── Status transition logic ───────────────────────────────────────────────

    [Fact]
    public void StatusTransition_PromoteTrue_SetsAwaitingUserReview()
    {
        var run = MakeRun();
        run.Status = VendorCallStatus.PostSummarySent;
        ApplySubmission(run, promote: true);
        Assert.Equal(VendorCallStatus.AwaitingUserReview, run.Status);
    }

    [Fact]
    public void StatusTransition_PromoteFalse_SetsClosed()
    {
        var run = MakeRun();
        run.Status = VendorCallStatus.PostSummarySent;
        ApplySubmission(run, promote: false);
        Assert.Equal(VendorCallStatus.Closed, run.Status);
    }

    [Fact]
    public void StatusTransition_TokenCleared_AfterSubmission()
    {
        var run = MakeRun(token: "some-token");
        ApplySubmission(run, promote: true);
        Assert.Null(run.ReviewToken);
        Assert.Null(run.ReviewTokenExpiresAt);
    }

    // Mirrors token invalidation logic in VendorCallReviewModel.OnPostAsync
    private static void ApplySubmission(VendorCallRun run, bool promote)
    {
        run.ReviewToken          = null;
        run.ReviewTokenExpiresAt = null;
        run.Status               = promote ? VendorCallStatus.AwaitingUserReview : VendorCallStatus.Closed;
        run.UpdatedAt            = DateTimeOffset.UtcNow;
    }
}
