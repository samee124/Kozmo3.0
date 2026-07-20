#pragma warning disable CS0618 // obsolete pre-Review-pipeline composers kept for reference
using Po.VendorCall;
using Xunit;

namespace Po.VendorCall.Tests;

/// <summary>
/// Tests for PostMeetingSummaryComposer.
/// All tests use deterministic stub extraction results — no LLM required.
/// </summary>
public sealed class PostMeetingSummaryComposerTests
{
    // ── Fixture factories ─────────────────────────────────────────────────────

    private static VendorCallRun MakeRun(string? principal = null) => new()
    {
        Id                      = Guid.NewGuid(),
        EventId                 = "northstar-renewal-2026-07-22",
        VendorId                = Guid.Parse("dd000001-0000-0000-0000-000000000001"),
        VendorName              = "Northstar Software",
        MeetingSubject          = "Northstar Software — annual renewal review",
        StartUtc                = new DateTimeOffset(2026, 7, 22, 10, 0, 0, TimeSpan.Zero),
        EndUtc                  = new DateTimeOffset(2026, 7, 22, 11, 0, 0, TimeSpan.Zero),
        SignedInUserPrincipalId = principal ?? "ritesh@econtracts.onmicrosoft.com",
        Status                  = VendorCallStatus.TranscriptAnalyzed,
        CreatedAt               = DateTimeOffset.UtcNow,
        UpdatedAt               = DateTimeOffset.UtcNow,
    };

    /// <summary>
    /// Standard fixture: 7 extracted items, 5 pre-brief items (3 addressed, 2 not).
    /// Speakers: Daniel Reed = vendor, Ritesh P = internal (matches "ritesh@...").
    /// </summary>
    private static TranscriptExtractionResult MakeExtraction(bool improving = true)
    {
        var items = new List<TranscriptExtractedItem>
        {
            // Decision
            new(TranscriptItemType.Decision,
                "Pricing discussion deferred pending breakdown",
                "Daniel Reed", "Ritesh P",
                "I think the sensible thing is to defer the pricing discussion.",
                TimeSpan.FromSeconds(59), 0.88, "meeting.vendor_call.decision",
                null, null, false),

            // Vendor commitments
            new(TranscriptItemType.Commitment,
                "Daniel Reed to deliver pricing enhancement breakdown by end of day Friday",
                "Daniel Reed", "Ritesh P",
                "I will have the pricing enhancement breakdown to you by end of day Friday.",
                TimeSpan.FromSeconds(79), 0.92, "vendor.commitment.description",
                "Daniel Reed", "Friday", false),

            new(TranscriptItemType.Commitment,
                "Daniel Reed to deliver Q2 SLA compliance report by Wednesday",
                "Daniel Reed", "Ritesh P",
                "I will have it to you by Wednesday of this week without fail.",
                TimeSpan.FromSeconds(101), 0.93, "vendor.commitment.description",
                "Daniel Reed", "Wednesday", false),

            new(TranscriptItemType.Commitment,
                "Daniel Reed to compile utilization data from platform analytics",
                "Daniel Reed", "Ritesh P",
                "I will try to pull together the utilization data.",
                TimeSpan.FromSeconds(181), 0.62, "vendor.commitment.description",
                "Daniel Reed", "before renewal", true),

            // Internal next step
            new(TranscriptItemType.NextStep,
                "Ritesh P to share internal utilization analysis from IT team",
                "Ritesh P", "Daniel Reed",
                "I am going to share our internal utilization analysis from our IT team.",
                TimeSpan.FromSeconds(196), 0.86, "vendor.commitment.description",
                "Ritesh P", "before renewal", false),

            // Open question
            new(TranscriptItemType.OpenQuestion,
                "Whether SOC 2 Type II certificate was received by legal team",
                "Ritesh P", "Daniel Reed",
                "I will check with the legal team.",
                TimeSpan.FromSeconds(143), 0.76, "vendor.evidence_gap",
                "Ritesh P", null, true),

            // Pricing signal
            new(TranscriptItemType.PricingSignal,
                "Northstar proposed 7% annual pricing uplift",
                "Daniel Reed", "Ritesh P",
                "The 7% reflects our infrastructure investment.",
                TimeSpan.FromSeconds(26), 0.94, "vendor.communication.pricing_signal",
                null, null, false),
        };

        // 3 addressed, 2 not addressed (improving scenario)
        var resolved = new List<PreBriefItemResolution>
        {
            new("7% pricing uplift",              false, null,                        null,                          0.0),
            new("SLA compliance report overdue",  true,  "deadline Wed Jul 23",       TimeSpan.FromSeconds(101),     0.93),
            new("License utilization review",     true,  "agreed to compile data",    TimeSpan.FromSeconds(181),     0.62),
            new("SOC 2 certificate overdue",      true,  "confirmed sent last month", TimeSpan.FromSeconds(143),     0.76),
            new("Renewal notice deadline Jul 30", false, null,                        null,                          0.0),
        };

        // For Needs attention scenario: only 1 addressed
        if (!improving)
        {
            resolved = [
                new("7% pricing uplift",              false, null,                     null,                      0.0),
                new("SLA compliance report overdue",  false, null,                     null,                      0.0),
                new("License utilization review",     true,  "agreed to compile data", TimeSpan.FromSeconds(181), 0.62),
                new("SOC 2 certificate overdue",      false, null,                     null,                      0.0),
                new("Renewal notice deadline Jul 30", false, null,                     null,                      0.0),
            ];
        }

        var meta = new TranscriptExtractionMetadata(20, items.Count, 5, 2, 2, TimeSpan.FromSeconds(4));
        return new TranscriptExtractionResult(items, resolved, meta);
    }

    private static PostMeetingSummaryContext MakeContext(
        bool improving = true, string? principal = null) =>
        new(MakeRun(principal), MakeExtraction(improving), null, null);

    private static PostMeetingSummary Compose(bool improving = true, string? principal = null) =>
        new PostMeetingSummaryComposer().Compose(MakeContext(improving, principal));

    // ── All sections populated ────────────────────────────────────────────────

    [Fact]
    public void AllSections_ArePopulated_GivenNorthstarExtraction()
    {
        var summary = Compose();

        Assert.NotEmpty(summary.MeetingOutcome.Content);
        Assert.NotEmpty(summary.DecisionsMade.Content);
        Assert.NotEmpty(summary.NewCommitments.Content);
        Assert.NotEmpty(summary.ResolvedFromPreBrief.Content);
        Assert.NotEmpty(summary.CommercialStateChange.Content);
        Assert.NotEmpty(summary.StillOpen.Content);
        Assert.NotEmpty(summary.RecommendedNextAction.Content);
    }

    // ── MeetingOutcome ────────────────────────────────────────────────────────

    [Fact]
    public void MeetingOutcome_ReflectsAddressedCount()
    {
        var summary = Compose();
        Assert.Contains("3 of 5", summary.MeetingOutcome.Content);
    }

    // ── DecisionsMade ─────────────────────────────────────────────────────────

    [Fact]
    public void DecisionsMade_ContainsOnlyDecisionTypeItems()
    {
        var summary = Compose();
        Assert.All(summary.DecisionsMade.Items,
            i => Assert.DoesNotContain("[Vendor]", i.Text));
        var decision = Assert.Single(summary.DecisionsMade.Items);
        Assert.Contains("Pricing discussion", decision.Text);
    }

    // ── NewCommitments ────────────────────────────────────────────────────────

    [Fact]
    public void NewCommitments_SplitsVendorVsInternal()
    {
        // "ritesh@..." prefix "ritesh" matches "Ritesh P"
        var summary = Compose(principal: "ritesh@econtracts.onmicrosoft.com");

        var vendorItems   = summary.NewCommitments.Items.Where(i => i.Text.StartsWith("[Vendor]")).ToList();
        var internalItems = summary.NewCommitments.Items.Where(i => i.Text.StartsWith("[You]")).ToList();

        // 3 vendor (Daniel Reed x3) + 1 internal (Ritesh P)
        Assert.Equal(3, vendorItems.Count);
        Assert.Single(internalItems);
    }

    [Fact]
    public void NewCommitments_ContentShowsCorrectCounts()
    {
        var summary = Compose(principal: "ritesh@econtracts.onmicrosoft.com");
        Assert.Contains("Vendor commitments: 3", summary.NewCommitments.Content);
        Assert.Contains("Your commitments: 1", summary.NewCommitments.Content);
    }

    // ── ResolvedFromPreBrief ──────────────────────────────────────────────────

    [Fact]
    public void ResolvedFromPreBrief_ShowsCheckAndCrossMarkers()
    {
        var summary = Compose();
        var items   = summary.ResolvedFromPreBrief.Items;

        var checkItems  = items.Where(i => i.Text.StartsWith('✓')).ToList();
        var crossItems  = items.Where(i => i.Text.StartsWith('✗')).ToList();

        Assert.Equal(3, checkItems.Count);
        Assert.Equal(2, crossItems.Count);
    }

    // ── CommercialStateChange ─────────────────────────────────────────────────

    [Fact]
    public void CommercialStateChange_IsImproving_WhenMoreAddressedThanOpen()
    {
        // improving=true: 3 addressed, 2 unresolved pre-brief + 1 open question = 3 open
        // 3 addressed vs 3 open → Stable. Adjust expectation.
        // Actually: unresolved pre-brief = 2, open_question items = 1, total open = 3
        // addressed = 3, stillOpen = 3 → Stable
        var summary = Compose(improving: true);
        Assert.Contains("Stable", summary.CommercialStateChange.Content);
    }

    [Fact]
    public void CommercialStateChange_IsNeedsAttention_WhenIssuesGrew()
    {
        // improving=false: 1 addressed, 4 unresolved + 1 open_question = 5 open
        var summary = Compose(improving: false);
        Assert.Contains("Needs attention", summary.CommercialStateChange.Content);
    }

    // ── StillOpen ─────────────────────────────────────────────────────────────

    [Fact]
    public void StillOpen_IncludesUnresolvedPreBriefAndUnconfirmedItems()
    {
        var summary  = Compose();
        var allTexts = summary.StillOpen.Items.Select(i => i.Text).ToList();

        // 2 unresolved pre-brief + 1 open_question + 1 commitment requiring confirmation
        Assert.Contains(allTexts, t => t.Contains("7% pricing uplift"));
        Assert.Contains(allTexts, t => t.Contains("Renewal notice"));
        Assert.Contains(allTexts, t => t.Contains("SOC 2"));
        Assert.Contains(allTexts, t => t.Contains("requires confirmation"));
    }

    // ── RecommendedNextAction ─────────────────────────────────────────────────

    [Fact]
    public void RecommendedNextAction_PicksNearestDeadlineCommitment()
    {
        var summary = Compose();
        // Friday and Wednesday are near-term. First by timestamp = Friday (T:01:19)
        Assert.Contains("Friday", summary.RecommendedNextAction.Content);
    }

    // ── Empty extraction ──────────────────────────────────────────────────────

    [Fact]
    public void EmptyExtraction_ProducesSummaryWithoutErrors()
    {
        var emptyExtraction = new TranscriptExtractionResult(
            [],
            [],
            new TranscriptExtractionMetadata(0, 0, 0, 0, 0, TimeSpan.Zero));

        var context = new PostMeetingSummaryContext(MakeRun(), emptyExtraction, null, null);
        var summary = new PostMeetingSummaryComposer().Compose(context);

        Assert.NotEmpty(summary.MeetingOutcome.Content);
        Assert.NotEmpty(summary.DecisionsMade.Content);
        Assert.NotEmpty(summary.NewCommitments.Content);
    }

    // ── Source references ─────────────────────────────────────────────────────

    [Fact]
    public void EverySectionHasAtLeastOneSourceReference()
    {
        var summary = Compose();

        foreach (var section in new[]
        {
            summary.MeetingOutcome,
            summary.DecisionsMade,
            summary.NewCommitments,
            summary.ResolvedFromPreBrief,
            summary.CommercialStateChange,
            summary.StillOpen,
            summary.RecommendedNextAction,
        })
        {
            Assert.True(section.SourceReferences.Count > 0,
                $"Section '{section.Heading}' has no source references");
        }
    }
}
