#pragma warning disable CS0618 // obsolete pre-Review-pipeline composers kept for reference
using Po.VendorCall;
using Xunit;

namespace Po.VendorCall.Tests;

public sealed class PostMeetingSummaryTextRendererTests
{
    // ── Fixture ───────────────────────────────────────────────────────────────

    private static PostMeetingSummary MakeSummary()
    {
        var items = new List<TranscriptExtractedItem>
        {
            new(TranscriptItemType.Decision,
                "Pricing deferred pending breakdown",
                "Daniel Reed", "Ritesh P",
                "Let us park that for now.",
                TimeSpan.FromSeconds(59), 0.88, "meeting.vendor_call.decision",
                null, null, false),

            new(TranscriptItemType.Commitment,
                "Pricing breakdown by Friday",
                "Daniel Reed", "Ritesh P",
                "I will have it to you by end of day Friday.",
                TimeSpan.FromSeconds(79), 0.92, "vendor.commitment.description",
                "Daniel Reed", "Friday", false),

            new(TranscriptItemType.OpenQuestion,
                "SOC 2 certificate receipt status",
                "Ritesh P", "Daniel Reed",
                "I will check with the legal team.",
                TimeSpan.FromSeconds(143), 0.76, "vendor.evidence_gap",
                "Ritesh P", null, true),
        };

        var resolved = new List<PreBriefItemResolution>
        {
            new("SLA compliance report", true,  "deadline Wednesday", TimeSpan.FromSeconds(101), 0.93),
            new("7% pricing uplift",     false, null,                 null,                       0.0),
        };

        var meta = new TranscriptExtractionMetadata(20, 3, 1, 1, 0, TimeSpan.FromSeconds(2));
        var extraction = new TranscriptExtractionResult(items, resolved, meta);

        var run = new VendorCallRun
        {
            Id                      = Guid.NewGuid(),
            EventId                 = "test-event",
            VendorId                = Guid.NewGuid(),
            VendorName              = "Northstar Software",
            MeetingSubject          = "Northstar Software — annual renewal review",
            StartUtc                = new DateTimeOffset(2026, 7, 22, 10, 0, 0, TimeSpan.Zero),
            EndUtc                  = new DateTimeOffset(2026, 7, 22, 11, 0, 0, TimeSpan.Zero),
            SignedInUserPrincipalId = "ritesh@econtracts.onmicrosoft.com",
            Status                  = VendorCallStatus.TranscriptAnalyzed,
            CreatedAt               = DateTimeOffset.UtcNow,
            UpdatedAt               = DateTimeOffset.UtcNow,
        };

        var context = new PostMeetingSummaryContext(run, extraction, null, null);
        return new PostMeetingSummaryComposer().Compose(context);
    }

    private static readonly PostMeetingSummary Summary = MakeSummary();
    private static readonly string             Rendered = PostMeetingSummaryTextRenderer.Render(Summary);

    // ── Heading presence ──────────────────────────────────────────────────────

    [Fact]
    public void Output_ContainsAllSectionHeadings()
    {
        Assert.Contains("MEETING OUTCOME",              Rendered);
        Assert.Contains("DECISIONS MADE",               Rendered);
        Assert.Contains("NEW COMMITMENTS",              Rendered);
        Assert.Contains("RESOLVED FROM PRE-MEETING BRIEF", Rendered);
        Assert.Contains("COMMERCIAL STATE CHANGE",      Rendered);
        Assert.Contains("STILL OPEN",                   Rendered);
        Assert.Contains("RECOMMENDED NEXT ACTION",      Rendered);
        Assert.Contains("SOURCES",                      Rendered);
    }

    // ── Transcript timestamp rendering ────────────────────────────────────────

    [Fact]
    public void Output_ContainsTranscriptTimestamps()
    {
        // Items have timestamps at 59s (01:59? No: 59s = 0min 59s = 00:59), 79s (01:19), 143s (02:23)
        Assert.Contains("[T:", Rendered);
    }

    // ── Warning marker ────────────────────────────────────────────────────────

    [Fact]
    public void WarningMarker_AppearsForRequiresConfirmationItems()
    {
        // OpenQuestion at T:02:23 has RequiresUserConfirmation = true
        Assert.Contains("⚠", Rendered);
    }

    // ── Citations section ─────────────────────────────────────────────────────

    [Fact]
    public void CitationsSection_ListsPreBriefAndTranscriptSources()
    {
        Assert.Contains("[Pre]", Rendered);
        Assert.Contains("[T:",   Rendered);
        Assert.Contains("Pre-meeting brief: Northstar Software", Rendered);
    }
}
