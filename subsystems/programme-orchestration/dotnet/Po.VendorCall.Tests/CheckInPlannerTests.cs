using If.Contracts;
using Po.VendorCall;
using Wc.Contracts;
using Xunit;

namespace Po.VendorCall.Tests;

public sealed class CheckInPlannerTests
{
    private static readonly Guid VendorId = Guid.Parse("dd000001-0000-0000-0000-000000000001");
    private static readonly DateTimeOffset Now = new(2026, 7, 15, 12, 0, 0, TimeSpan.Zero);
    private const string EventId = "evt-northstar-renewal-001";

    private static VendorCallContext MakeContext(string externalEventId = EventId)
    {
        var recipe = new VendorCallRecipe(
            RecipeId: "vendor-call-v1", Version: "1.0", MeetingType: "vendor_call",
            CalendarWindowDays: 14, EmailLookbackDays: 90,
            PreMeetingCheckInHours: 24, BriefingOffsetMinutes: 10,
            PostMeetingCheckInMinutes: 15,
            Sections: [],
            Limits: new RecipeLimits(30, 2, 6));

        var meeting = new CalendarArtifact(
            ArtifactId:        Guid.NewGuid(),
            SourceSystem:      "graph",
            SourceType:        "calendar_event",
            TenantId:          "test-tenant",
            SourcePrincipalId: "user-1",
            ExternalId:        externalEventId,
            ICalUid:           "ical-001",
            Subject:           "Northstar renewal review",
            StartUtc:          Now.AddDays(1),
            EndUtc:            Now.AddDays(1).AddHours(1),
            Organizer:         "rishi@econtracts.onmicrosoft.com",
            Attendees:         ["rishi@econtracts.onmicrosoft.com", "alex.hamilton@northstarsoftware.com"],
            BodyPreview:       "Annual renewal review",
            CapturedAtUtc:     Now);

        var match = new VendorEntityMatchResult(
            VendorId:   VendorId,
            VendorName: "Northstar Software",
            MatchType:  VendorMatchType.DomainExact,
            MatchScore: 0.95);

        return new VendorCallContext(
            Meeting:                 meeting,
            Match:                   match,
            VendorDomains:           ["northstarsoftware.com"],
            SignedInUserPrincipalId: "user-1",
            Recipe:                  recipe);
    }

    private static VendorCallEvidenceBundle EmptyBundle() =>
        new(RecentEmails:        [],
            FilteredNoiseEmails: [],
            Contracts:           [],
            PriorMeetingNotes:   [],
            OpenCommitments:     [],
            CommercialSignals:   [],
            EvidenceGaps:        []);

    private static IReadOnlyList<VendorCallQuestion> QuestionBank() =>
    [
        new VendorCallQuestion(
            QuestionId: "vendor_reality_change_pre_v1",
            ClaimKey:   "vendor_reality_change",
            Stage:      "pre_meeting",
            Prompt:     "Before your Northstar meeting: are you expecting any surprises or changes to the commercial relationship?",
            Answers:    ["No surprises expected", "Some concerns", "Significant concerns", "Expecting a difficult conversation", "Not sure"],
            ExpiryDays: 14),
        new VendorCallQuestion(
            QuestionId: "vendor_meeting_outcome_v1",
            ClaimKey:   "vendor_meeting_outcome",
            Stage:      "post_meeting",
            Prompt:     "How did the Northstar meeting go overall?",
            Answers:    ["Very positive", "Positive", "Neutral", "Challenging", "Very challenging"],
            ExpiryDays: 1),
    ];

    private static VendorCallCheckInPlanner MakePlanner()
        => new("rishi@econtracts.onmicrosoft.com");

    // ── Happy path ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Plan_NoExistingCheckIn_ReturnsDispatched()
    {
        var store     = new InMemoryCheckInStore();
        var transport = new RecordingCheckInTransport();
        var planner   = MakePlanner();

        var result = await planner.PlanPreMeetingAsync(
            MakeContext(), EmptyBundle(), QuestionBank(), store, transport, Now);

        Assert.Equal(CheckInDispatchStatus.Dispatched, result.Status);
        Assert.NotNull(result.CheckIn);
    }

    [Fact]
    public async Task Plan_CreatedCheckIn_PersistedToStore()
    {
        var store     = new InMemoryCheckInStore();
        var transport = new RecordingCheckInTransport();
        var planner   = MakePlanner();

        await planner.PlanPreMeetingAsync(
            MakeContext(), EmptyBundle(), QuestionBank(), store, transport, Now);

        var open = await store.GetOpenAsync();
        var saved = Assert.Single(open);

        Assert.Equal(VendorId,               saved.VendorId);
        Assert.Equal(CheckInKind.DIMENSION_GAP, saved.Kind);
        Assert.Equal(PendingStatus.OPEN,      saved.Status);
    }

    [Fact]
    public async Task Plan_CreatedCheckIn_HasCorrectTargetField()
    {
        var store     = new InMemoryCheckInStore();
        var transport = new RecordingCheckInTransport();
        var planner   = MakePlanner();

        await planner.PlanPreMeetingAsync(
            MakeContext(), EmptyBundle(), QuestionBank(), store, transport, Now);

        var open  = await store.GetOpenAsync();
        var saved = Assert.Single(open);

        Assert.Equal($"vendorcall_pre:{EventId}", saved.TargetField);
    }

    [Fact]
    public async Task Plan_CreatedCheckIn_ExpiresAtExpectedTime()
    {
        var store     = new InMemoryCheckInStore();
        var transport = new RecordingCheckInTransport();
        var planner   = MakePlanner();

        await planner.PlanPreMeetingAsync(
            MakeContext(), EmptyBundle(), QuestionBank(), store, transport, Now);

        var open  = await store.GetOpenAsync();
        var saved = Assert.Single(open);

        // pre_meeting question expiryDays = 14
        Assert.Equal(Now.AddDays(14), saved.ExpiresAt);
    }

    [Fact]
    public async Task Plan_TransportReceivesCheckIn()
    {
        var store     = new InMemoryCheckInStore();
        var transport = new RecordingCheckInTransport();
        var planner   = MakePlanner();

        var result = await planner.PlanPreMeetingAsync(
            MakeContext(), EmptyBundle(), QuestionBank(), store, transport, Now);

        Assert.Single(transport.SentBatches);
        Assert.Single(transport.SentBatches[0]);
        Assert.Equal(result.CheckIn!.CheckInId, transport.SentBatches[0][0].CheckInId);
    }

    // ── Idempotency ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Plan_ExistingOpenCheckIn_ReturnsAlreadyDispatched()
    {
        var store     = new InMemoryCheckInStore();
        var transport = new RecordingCheckInTransport();
        var planner   = MakePlanner();

        // First call — creates check-in
        await planner.PlanPreMeetingAsync(
            MakeContext(), EmptyBundle(), QuestionBank(), store, transport, Now);

        // Second call — same meeting, same vendor → idempotent
        var result = await planner.PlanPreMeetingAsync(
            MakeContext(), EmptyBundle(), QuestionBank(), store, transport, Now);

        Assert.Equal(CheckInDispatchStatus.AlreadyDispatched, result.Status);
        Assert.Null(result.CheckIn);

        // Transport was called once (first call only)
        Assert.Single(transport.SentBatches);
    }

    [Fact]
    public async Task Plan_DifferentMeeting_RaisesNewCheckIn()
    {
        var store     = new InMemoryCheckInStore();
        var transport = new RecordingCheckInTransport();
        var planner   = MakePlanner();

        await planner.PlanPreMeetingAsync(
            MakeContext("evt-meeting-1"), EmptyBundle(), QuestionBank(), store, transport, Now);

        var result = await planner.PlanPreMeetingAsync(
            MakeContext("evt-meeting-2"), EmptyBundle(), QuestionBank(), store, transport, Now);

        Assert.Equal(CheckInDispatchStatus.Dispatched, result.Status);
        Assert.Equal(2, transport.SentBatches.Count);
    }

    // ── No questions available ────────────────────────────────────────────────

    [Fact]
    public async Task Plan_EmptyQuestionBank_ReturnsNoQuestionsAvailable()
    {
        var store     = new InMemoryCheckInStore();
        var transport = new RecordingCheckInTransport();
        var planner   = MakePlanner();

        var result = await planner.PlanPreMeetingAsync(
            MakeContext(), EmptyBundle(), [], store, transport, Now);

        Assert.Equal(CheckInDispatchStatus.NoQuestionsAvailable, result.Status);
        Assert.Null(result.CheckIn);
        Assert.Empty(transport.SentBatches);
    }

    [Fact]
    public async Task Plan_OnlyPostMeetingQuestions_ReturnsNoQuestionsAvailable()
    {
        var store     = new InMemoryCheckInStore();
        var transport = new RecordingCheckInTransport();
        var planner   = MakePlanner();

        IReadOnlyList<VendorCallQuestion> postOnly =
        [
            new("vendor_meeting_outcome_v1", "vendor_meeting_outcome", "post_meeting",
                "How did it go?", ["Good", "Bad"], ExpiryDays: 1)
        ];

        var result = await planner.PlanPreMeetingAsync(
            MakeContext(), EmptyBundle(), postOnly, store, transport, Now);

        Assert.Equal(CheckInDispatchStatus.NoQuestionsAvailable, result.Status);
    }

    // ── Transport failure ─────────────────────────────────────────────────────

    [Fact]
    public async Task Plan_TransportThrows_CheckInStillPersisted()
    {
        var store     = new InMemoryCheckInStore();
        var transport = new FailingCheckInTransport();
        var planner   = MakePlanner();

        // Should not throw
        var result = await planner.PlanPreMeetingAsync(
            MakeContext(), EmptyBundle(), QuestionBank(), store, transport, Now);

        Assert.Equal(CheckInDispatchStatus.Dispatched, result.Status);

        var open = await store.GetOpenAsync();
        Assert.Single(open);
    }
}

// ── Test doubles ──────────────────────────────────────────────────────────────

/// <summary>In-memory ICheckInStore for test use.</summary>
internal sealed class InMemoryCheckInStore : ICheckInStore
{
    private readonly List<CheckIn> _checkIns = [];

    public Task SaveAsync(CheckIn checkIn, CancellationToken ct = default)
    {
        _checkIns.RemoveAll(c => c.CheckInId == checkIn.CheckInId);
        _checkIns.Add(checkIn);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<CheckIn>> GetOpenAsync(CancellationToken ct = default)
    {
        IReadOnlyList<CheckIn> open = _checkIns
            .Where(c => c.Status == PendingStatus.OPEN)
            .ToList();
        return Task.FromResult(open);
    }

    public Task<CheckIn?> GetAsync(Guid checkInId, CancellationToken ct = default)
    {
        CheckIn? found = _checkIns.FirstOrDefault(c => c.CheckInId == checkInId);
        return Task.FromResult(found);
    }

    public Task<IReadOnlyList<CheckIn>> GetResolvedForVendorAsync(Guid vendorId, CancellationToken ct = default)
    {
        IReadOnlyList<CheckIn> resolved = _checkIns
            .Where(c => c.VendorId == vendorId &&
                        c.Status is PendingStatus.PROCESSED or PendingStatus.EXPIRED)
            .OrderBy(c => c.RaisedAt)
            .ToList();
        return Task.FromResult(resolved);
    }
}

/// <summary>ICheckInTransport that records every SendAsync call.</summary>
internal sealed class RecordingCheckInTransport : ICheckInTransport
{
    public List<IReadOnlyList<CheckIn>> SentBatches { get; } = [];

    public Task SendAsync(IReadOnlyList<CheckIn> checkIns, CancellationToken ct = default)
    {
        SentBatches.Add(checkIns);
        return Task.CompletedTask;
    }
}

/// <summary>ICheckInTransport that always throws, to test transport-failure resilience.</summary>
internal sealed class FailingCheckInTransport : ICheckInTransport
{
    public Task SendAsync(IReadOnlyList<CheckIn> checkIns, CancellationToken ct = default)
        => throw new InvalidOperationException("Simulated transport failure.");
}
