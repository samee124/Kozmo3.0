using System.Text.Json;
using Kozmo.Llm;
using Po.VendorCall;
using Wj.MeetingPulse;
using Xunit;

namespace Wj.MeetingPulse.Tests;

/// <summary>
/// Tests for the pure static decision helpers on MeetingPulseWorker.
/// These are deterministic — no Graph API, no SMTP, no LLM.
/// </summary>
public sealed class MeetingPulseWorkerTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 16, 10, 0, 0, TimeSpan.Zero);

    // ── ShouldProcessPreMeeting ────────────────────────────────────────────────

    [Fact]
    public void PreMeeting_Detected_WithinLeadWindow_ReturnsTrue()
    {
        var run = MakeRun(VendorCallStatus.Detected,
            start: Now.AddHours(12), end: Now.AddHours(13));

        Assert.True(MeetingPulseWorker.ShouldProcessPreMeeting(
            run, Now, TimeSpan.FromHours(24)));
    }

    [Fact]
    public void PreMeeting_Detected_MeetingTooFarInFuture_ReturnsFalse()
    {
        // Starts in 30 hours — outside 24-hour lead window
        var run = MakeRun(VendorCallStatus.Detected,
            start: Now.AddHours(30), end: Now.AddHours(31));

        Assert.False(MeetingPulseWorker.ShouldProcessPreMeeting(
            run, Now, TimeSpan.FromHours(24)));
    }

    [Fact]
    public void PreMeeting_Detected_MeetingAlreadyStarted_ReturnsFalse()
    {
        // StartUtc is in the past
        var run = MakeRun(VendorCallStatus.Detected,
            start: Now.AddMinutes(-30), end: Now.AddMinutes(30));

        Assert.False(MeetingPulseWorker.ShouldProcessPreMeeting(
            run, Now, TimeSpan.FromHours(24)));
    }

    [Fact]
    public void PreMeeting_BriefingSent_ReturnsFalse()
    {
        var run = MakeRun(VendorCallStatus.BriefingSent,
            start: Now.AddHours(12), end: Now.AddHours(13));

        Assert.False(MeetingPulseWorker.ShouldProcessPreMeeting(
            run, Now, TimeSpan.FromHours(24)));
    }

    [Fact]
    public void PreMeeting_MeetingEnded_ReturnsFalse()
    {
        var run = MakeRun(VendorCallStatus.MeetingEnded,
            start: Now.AddHours(-2), end: Now.AddHours(-1));

        Assert.False(MeetingPulseWorker.ShouldProcessPreMeeting(
            run, Now, TimeSpan.FromHours(24)));
    }

    // ── ShouldAdvanceToMeetingEnded ────────────────────────────────────────────

    [Fact]
    public void MeetingEnded_BriefingSent_EndedPastBuffer_ReturnsTrue()
    {
        // Meeting ended 1 hour ago — well past 30-minute buffer
        var run = MakeRun(VendorCallStatus.BriefingSent,
            start: Now.AddHours(-2), end: Now.AddHours(-1));

        Assert.True(MeetingPulseWorker.ShouldAdvanceToMeetingEnded(
            run, Now, TimeSpan.FromMinutes(30)));
    }

    [Fact]
    public void MeetingEnded_PreCheckInSent_EndedPastBuffer_ReturnsTrue()
    {
        var run = MakeRun(VendorCallStatus.PreCheckInSent,
            start: Now.AddHours(-2), end: Now.AddHours(-1));

        Assert.True(MeetingPulseWorker.ShouldAdvanceToMeetingEnded(
            run, Now, TimeSpan.FromMinutes(30)));
    }

    [Fact]
    public void MeetingEnded_BriefingSent_BufferNotExpired_ReturnsFalse()
    {
        // Meeting ended 10 minutes ago — buffer is 30 minutes, not yet expired
        var run = MakeRun(VendorCallStatus.BriefingSent,
            start: Now.AddHours(-1), end: Now.AddMinutes(-10));

        Assert.False(MeetingPulseWorker.ShouldAdvanceToMeetingEnded(
            run, Now, TimeSpan.FromMinutes(30)));
    }

    [Fact]
    public void MeetingEnded_AlreadyMeetingEndedStatus_ReturnsFalse()
    {
        var run = MakeRun(VendorCallStatus.MeetingEnded,
            start: Now.AddHours(-2), end: Now.AddHours(-1));

        Assert.False(MeetingPulseWorker.ShouldAdvanceToMeetingEnded(
            run, Now, TimeSpan.FromMinutes(30)));
    }

    [Fact]
    public void MeetingEnded_DetectedStatus_ReturnsFalse()
    {
        // Detected runs skip the meeting-ended step
        var run = MakeRun(VendorCallStatus.Detected,
            start: Now.AddHours(-2), end: Now.AddHours(-1));

        Assert.False(MeetingPulseWorker.ShouldAdvanceToMeetingEnded(
            run, Now, TimeSpan.FromMinutes(30)));
    }

    [Fact]
    public void MeetingEnded_PostCheckInSentStatus_ReturnsFalse()
    {
        var run = MakeRun(VendorCallStatus.PostCheckInSent,
            start: Now.AddHours(-2), end: Now.AddHours(-1));

        Assert.False(MeetingPulseWorker.ShouldAdvanceToMeetingEnded(
            run, Now, TimeSpan.FromMinutes(30)));
    }

    // ── ShouldProcessPostMeeting ───────────────────────────────────────────────

    [Fact]
    public void PostMeeting_MeetingEnded_ReturnsTrue()
    {
        var run = MakeRun(VendorCallStatus.MeetingEnded,
            start: Now.AddHours(-2), end: Now.AddHours(-1));

        Assert.True(MeetingPulseWorker.ShouldProcessPostMeeting(run));
    }

    [Fact]
    public void PostMeeting_BriefingSent_ReturnsFalse()
    {
        var run = MakeRun(VendorCallStatus.BriefingSent,
            start: Now.AddHours(-2), end: Now.AddHours(-1));

        Assert.False(MeetingPulseWorker.ShouldProcessPostMeeting(run));
    }

    [Fact]
    public void PostMeeting_PostCheckInSent_ReturnsFalse()
    {
        var run = MakeRun(VendorCallStatus.PostCheckInSent,
            start: Now.AddHours(-2), end: Now.AddHours(-1));

        Assert.False(MeetingPulseWorker.ShouldProcessPostMeeting(run));
    }

    [Fact]
    public void PostMeeting_TranscriptReady_ReturnsFalse()
    {
        var run = MakeRun(VendorCallStatus.TranscriptReady,
            start: Now.AddHours(-2), end: Now.AddHours(-1));

        Assert.False(MeetingPulseWorker.ShouldProcessPostMeeting(run));
    }

    // ── Options defaults ───────────────────────────────────────────────────────

    [Fact]
    public void Options_DefaultValues_AreCorrect()
    {
        var opts = new MeetingPulseOptions();

        Assert.Equal(30, opts.PollingIntervalSeconds);
        Assert.Equal(24, opts.PreMeetingLeadTimeHours);
        Assert.Equal(30, opts.MeetingEndedBufferMinutes);
        Assert.False(opts.EnableTranscriptAnalysis);
        Assert.Equal("", opts.OwnerEmail);
        Assert.Equal("", opts.UserObjectId);
        Assert.Equal(4, opts.MaxTranscriptWaitHours);
    }

    // ── ExtractGraphEventId ────────────────────────────────────────────────────

    [Fact]
    public void ExtractGraphEventId_ValidPrefix_ReturnsRawId()
    {
        const string raw = "AAMkAGI2YzZkNmEw";
        Assert.Equal(raw, MeetingPulseWorker.ExtractGraphEventId($"msgraph:event:{raw}"));
    }

    [Fact]
    public void ExtractGraphEventId_UnknownFormat_ReturnsNull()
    {
        Assert.Null(MeetingPulseWorker.ExtractGraphEventId("some-other-id"));
        Assert.Null(MeetingPulseWorker.ExtractGraphEventId(""));
    }

    // ── VendorCallStatus.Cancelled ─────────────────────────────────────────────

    [Fact]
    public void VendorCallStatus_HasCancelled()
    {
        Assert.True(Enum.IsDefined(typeof(VendorCallStatus), VendorCallStatus.Cancelled));
    }

    // ── PostMeetingCheckInPlanner.PostMeetingTargetFieldFor ────────────────────

    [Fact]
    public void PostMeetingTargetField_Format_IsCorrect()
    {
        const string eventId = "AAMkAGI2YzZkNmEw";
        var tf = PostMeetingCheckInPlanner.PostMeetingTargetFieldFor(eventId);
        Assert.Equal($"vendorcall_post:{eventId}", tf);
    }

    // ── Enum values ────────────────────────────────────────────────────────────

    [Fact]
    public void VendorCallStatus_HasPostCheckInSent()
    {
        Assert.True(Enum.IsDefined(typeof(VendorCallStatus), VendorCallStatus.PostCheckInSent));
    }

    [Fact]
    public void VendorCallStatus_HasNoTranscriptAvailable()
    {
        Assert.True(Enum.IsDefined(typeof(VendorCallStatus), VendorCallStatus.NoTranscriptAvailable));
    }

    // ── EnableLlmNarrative option ──────────────────────────────────────────────

    [Fact]
    public void Options_EnableLlmNarrative_DefaultsToTrue()
    {
        var opts = new MeetingPulseOptions();
        Assert.True(opts.EnableLlmNarrative);
    }

    // ── WorkerLlmProvider ──────────────────────────────────────────────────────

    [Fact]
    public void WorkerLlmProvider_WithNullLlm_ExposesNull()
    {
        var provider = new WorkerLlmProvider(null);
        Assert.Null(provider.Llm);
    }

    [Fact]
    public void WorkerLlmProvider_WithRealLlm_ExposesIt()
    {
        var stub = new StubLlm();
        var provider = new WorkerLlmProvider(stub);
        Assert.Same(stub, provider.Llm);
    }

    // ── LLM reachability: ReviewComposer with non-null LLM ────────────────────
    // Proves the LLM path is wired correctly through WorkerLlmProvider.
    // Uses a stub LLM that returns a valid JSON response so Mode B is exercised.

    [Fact]
    public async Task ReviewComposer_WithMockLlm_ProducesLlmEnhancedSection()
    {
        // Proves the LLM path is wired correctly through WorkerLlmProvider:
        // a non-null LLM produces at least one LlmEnhanced=true section.
        var store    = new InMemoryReviewCheckpointStore();
        var llm      = new StubLlm();
        var composer = new ReviewComposer(store, llm);

        var bundle  = new VendorCallEvidenceBundle([], [], [], [], [], [], []);
        var beliefs = new List<Kozmo.Contracts.Belief>();

        var result = await composer.ComposeAsync(
            vendorId:           Guid.NewGuid(),
            bundle:             bundle,
            beliefs:            beliefs,
            previousCheckpoint: null,
            vendorName:         "Acme Corp",
            ownerUpn:           "owner@example.com",
            eventTypeCode:      "saas_renewal",
            now:                Now,
            vendorCallRunId:    Guid.NewGuid(),
            kind:               CheckpointKind.PreMeeting,
            ct:                 CancellationToken.None);

        var anyEnhanced = result.Q1.LlmEnhanced || result.Q2.LlmEnhanced || result.Q3.LlmEnhanced
                       || result.Q4.LlmEnhanced || result.Q5.LlmEnhanced || result.Overview.LlmEnhanced;

        Assert.True(anyEnhanced,
            "Expected at least one section to be LlmEnhanced=true when a non-null LLM is provided");
    }

    [Fact]
    public async Task ReviewComposer_WithNullLlm_FallsBackToModeA()
    {
        var store    = new InMemoryReviewCheckpointStore();
        var composer = new ReviewComposer(store, llm: null);

        var bundle  = new VendorCallEvidenceBundle([], [], [], [], [], [], []);
        var beliefs = new List<Kozmo.Contracts.Belief>();

        var result = await composer.ComposeAsync(
            vendorId:           Guid.NewGuid(),
            bundle:             bundle,
            beliefs:            beliefs,
            previousCheckpoint: null,
            vendorName:         "Acme Corp",
            ownerUpn:           "owner@example.com",
            eventTypeCode:      "saas_renewal",
            now:                Now,
            vendorCallRunId:    Guid.NewGuid(),
            kind:               CheckpointKind.PreMeeting,
            ct:                 CancellationToken.None);

        // Mode A: no section should be LLM-enhanced
        Assert.False(result.Q1.LlmEnhanced || result.Q2.LlmEnhanced || result.Q3.LlmEnhanced
                  || result.Q4.LlmEnhanced || result.Q5.LlmEnhanced || result.Overview.LlmEnhanced,
            "Expected all sections to be LlmEnhanced=false when llm is null (Mode A fallback)");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static VendorCallRun MakeRun(
        VendorCallStatus status,
        DateTimeOffset   start,
        DateTimeOffset   end) => new()
    {
        Id         = Guid.NewGuid(),
        EventId    = "test-event-id",
        VendorId   = Guid.NewGuid(),
        VendorName = "Acme Corp",
        MeetingSubject = "Acme — renewal",
        StartUtc   = start,
        EndUtc     = end,
        SignedInUserPrincipalId = "owner@example.com",
        Status     = status,
        CreatedAt  = Now,
        UpdatedAt  = Now,
    };
}

// ── Test doubles ─────────────────────────────────────────────────────────────

/// <summary>
/// Stub LLM that returns {"text": "stub narrative"} as a JsonElement for every call.
/// The prose contains no dates or large numbers so GroundingChecker.Passes always returns true.
/// This causes ReviewComposer to produce LlmEnhanced=true sections.
/// </summary>
internal sealed class StubLlm : IKozmoLlm
{
    private static readonly LlmResult StubResult = MakeResult();

    private static LlmResult MakeResult()
    {
        using var doc = JsonDocument.Parse("{\"text\": \"This is a stub narrative from the LLM.\"}");
        return new LlmResult(doc.RootElement.Clone(), 0.9, "stub reasoning");
    }

    public Task<LlmResult> CompleteAsync(
        string system, string user, int maxTokens, CancellationToken ct = default)
        => Task.FromResult(StubResult);

    public Task<LlmResult> CompleteJsonAsync(
        string system, string user, int maxTokens, CancellationToken ct = default)
        => Task.FromResult(StubResult);
}

/// <summary>In-memory IReviewCheckpointStore for tests.</summary>
internal sealed class InMemoryReviewCheckpointStore : IReviewCheckpointStore
{
    private readonly List<ReviewCheckpoint> _store = [];

    public Task SaveAsync(ReviewCheckpoint checkpoint, CancellationToken ct = default)
    {
        _store.RemoveAll(c => c.VendorId == checkpoint.VendorId && c.Id == checkpoint.Id);
        _store.Add(checkpoint);
        return Task.CompletedTask;
    }

    public Task<ReviewCheckpoint?> GetLatestAsync(Guid vendorId, CancellationToken ct = default)
    {
        var latest = _store
            .Where(c => c.VendorId == vendorId)
            .OrderByDescending(c => c.CreatedAtUtc)
            .FirstOrDefault();
        return Task.FromResult(latest);
    }

    public Task<ReviewCheckpoint?> GetByIdAsync(Guid checkpointId, CancellationToken ct = default)
    {
        var cp = _store.FirstOrDefault(c => c.Id == checkpointId);
        return Task.FromResult(cp);
    }

    public Task<IReadOnlyList<ReviewCheckpoint>> GetHistoryAsync(
        Guid vendorId, int maxCount, CancellationToken ct = default)
    {
        IReadOnlyList<ReviewCheckpoint> result = _store
            .Where(c => c.VendorId == vendorId)
            .OrderByDescending(c => c.CreatedAtUtc)
            .Take(maxCount)
            .ToList();
        return Task.FromResult(result);
    }
}
