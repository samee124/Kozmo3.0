using Po.VendorCall;
using Xunit;

namespace Po.VendorCall.Tests;

/// <summary>
/// Tests for PreMeetingReviewEmailRenderer, PostMeetingReviewEmailRenderer,
/// and ReviewEmailContextBuilder helpers.
/// </summary>
public sealed class ReviewEmailRendererTests
{
    // ── Test fixtures ─────────────────────────────────────────────────────────

    private static ReviewCheckpoint MakeCheckpoint(
        ReviewStatus status   = ReviewStatus.Green,
        ReviewMovement movement = ReviewMovement.Stable,
        ReviewConfidence confidence = ReviewConfidence.High) =>
        new ReviewCheckpoint(
            Id:                     Guid.NewGuid(),
            VendorId:               Guid.NewGuid(),
            VendorCallRunId:        null,
            Kind:                   CheckpointKind.PreMeeting,
            CreatedAtUtc:           new DateTimeOffset(2026, 7, 1, 9, 0, 0, TimeSpan.Zero),
            Status:                 status,
            Movement:               movement,
            Confidence:             confidence,
            Q1Answer:               "Q1 answer text.",
            Q2Answer:               "Q2 answer text.",
            Q3Answer:               "Q3 answer text.",
            Q4Answer:               "Q4 answer text.",
            Q5Answer:               "Q5 answer text.",
            OpenCommitmentCount:    2,
            OverdueCommitmentCount: 0,
            UnresolvedSignalCount:  1,
            SourceReferenceIds:     ["ref-001", "ref-002"]);

    private static ReviewEmailContext MakeContext(
        ReviewCheckpoint? checkpoint = null,
        string vendorName = "Northstar Software",
        DateTimeOffset? previousReview = null) =>
        new ReviewEmailContext(
            VendorName:             vendorName,
            ContractSummary:        "MSA 2026 — GBP 285,000/yr",
            MeetingTimeUtc:         new DateTimeOffset(2026, 7, 16, 14, 0, 0, TimeSpan.Zero),
            PreviousReviewDateUtc:  previousReview,
            RenewalStagePhrase:     "Pre-renewal, notice window open",
            ProposedSummary:        "7% pricing uplift outstanding",
            CurrentPositionSummary: "GBP 285,000/yr · renewal Sep 28",
            Checkpoint:             checkpoint ?? MakeCheckpoint(),
            ViewEvidenceUrl:        "https://kozmo.local/vendor-calls/run-1/evidence?token=tok",
            PostUpdateUrl:          "https://kozmo.local/vendor-calls/run-1/update?token=tok",
            FlagUrl:                "https://kozmo.local/vendor-calls/run-1/flag?token=tok");

    private static Q2FactPacket MakeQ2Packet(bool hasContracts = true, bool hasSignals = true) =>
        new Q2FactPacket(
            VendorName:       "Northstar Software",
            Contracts: hasContracts
                ? [new ContractFact("MSA", 285_000m, "GBP", "2026-09-28", null, "c1")]
                : [],
            OpenCommitments:  [],
            CommercialSignals: hasSignals
                ? [new SignalFact("Pricing uplift outstanding", "s1")]
                : [],
            EvidenceGaps:     [],
            UpdateNotes:      [],
            PreviousAnswer:   null,
            IsFirstReview:    false,
            SourceReferenceIds: []);

    // ── PreMeetingReviewEmailRenderer ─────────────────────────────────────────

    [Fact]
    public void PreMeeting_Subject_ContainsVendorNameAndMinutes()
    {
        var renderer = new PreMeetingReviewEmailRenderer();
        var email    = renderer.Render(MakeContext(), minutesUntilMeeting: 30);
        Assert.Contains("Northstar Software", email.Subject);
        Assert.Contains("30 minutes", email.Subject);
    }

    [Fact]
    public void PreMeeting_HtmlBody_ContainsVendorName()
    {
        var renderer = new PreMeetingReviewEmailRenderer();
        var email    = renderer.Render(MakeContext(), minutesUntilMeeting: 10);
        Assert.Contains("Northstar Software", email.HtmlBody);
    }

    [Fact]
    public void PreMeeting_HtmlBody_ContainsQ1ThroughQ5Headings()
    {
        var renderer = new PreMeetingReviewEmailRenderer();
        var email    = renderer.Render(MakeContext(), minutesUntilMeeting: 10);
        Assert.Contains("Q1.", email.HtmlBody);
        Assert.Contains("Q5.", email.HtmlBody);
    }

    [Fact]
    public void PreMeeting_HtmlBody_ContainsViewEvidenceLink()
    {
        var renderer = new PreMeetingReviewEmailRenderer();
        var email    = renderer.Render(MakeContext(), minutesUntilMeeting: 10);
        Assert.Contains("vendor-calls/run-1/evidence", email.HtmlBody);
    }

    [Fact]
    public void PreMeeting_HtmlBody_ContainsPostUpdateLink()
    {
        var renderer = new PreMeetingReviewEmailRenderer();
        var email    = renderer.Render(MakeContext(), minutesUntilMeeting: 10);
        Assert.Contains("vendor-calls/run-1/update", email.HtmlBody);
    }

    [Fact]
    public void PreMeeting_HtmlBody_ContainsFlagLine()
    {
        var renderer = new PreMeetingReviewEmailRenderer();
        var email    = renderer.Render(MakeContext(), minutesUntilMeeting: 10);
        Assert.Contains("Flag this brief", email.HtmlBody);
    }

    [Fact]
    public void PreMeeting_HtmlBody_ContainsProposedAndCurrentStrip()
    {
        var renderer = new PreMeetingReviewEmailRenderer();
        var email    = renderer.Render(MakeContext(), minutesUntilMeeting: 10);
        Assert.Contains("Proposed / at risk", email.HtmlBody);
        Assert.Contains("Current position", email.HtmlBody);
    }

    [Fact]
    public void PreMeeting_PlainText_ContainsStatusAndVendorName()
    {
        var renderer = new PreMeetingReviewEmailRenderer();
        var email    = renderer.Render(MakeContext(), minutesUntilMeeting: 10);
        Assert.Contains("Northstar Software", email.PlainTextBody);
        Assert.Contains("STATUS:", email.PlainTextBody);
    }

    [Fact]
    public void PreMeeting_PlainText_ContainsViewEvidenceUrl()
    {
        var renderer = new PreMeetingReviewEmailRenderer();
        var email    = renderer.Render(MakeContext(), minutesUntilMeeting: 10);
        Assert.Contains("vendor-calls/run-1/evidence", email.PlainTextBody);
    }

    [Fact]
    public void PreMeeting_NoPreviousReview_ShowsFirstTrackedReview()
    {
        var renderer = new PreMeetingReviewEmailRenderer();
        var ctx      = MakeContext(previousReview: null);
        var email    = renderer.Render(ctx, minutesUntilMeeting: 10);
        Assert.Contains("First tracked review", email.HtmlBody);
    }

    [Fact]
    public void PreMeeting_WithPreviousReview_ShowsDate()
    {
        var prev     = new DateTimeOffset(2026, 4, 1, 10, 0, 0, TimeSpan.Zero);
        var renderer = new PreMeetingReviewEmailRenderer();
        var ctx      = MakeContext(previousReview: prev);
        var email    = renderer.Render(ctx, minutesUntilMeeting: 10);
        Assert.Contains("2026", email.HtmlBody);
    }

    // ── PostMeetingReviewEmailRenderer ────────────────────────────────────────

    [Fact]
    public void PostMeeting_Subject_ContainsVendorNameAndMeetingSummary()
    {
        var renderer = new PostMeetingReviewEmailRenderer();
        var email    = renderer.Render(MakeContext());
        Assert.Contains("Northstar Software", email.Subject);
        Assert.Contains("meeting summary", email.Subject);
    }

    [Fact]
    public void PostMeeting_HtmlBody_ContainsFlagLine()
    {
        var renderer = new PostMeetingReviewEmailRenderer();
        var email    = renderer.Render(MakeContext());
        Assert.Contains("Review and confirm this summary", email.HtmlBody);
    }

    [Fact]
    public void PostMeeting_HtmlBody_ContainsQ1ThroughQ5()
    {
        var renderer = new PostMeetingReviewEmailRenderer();
        var email    = renderer.Render(MakeContext());
        Assert.Contains("Q1 answer text.", email.HtmlBody);
        Assert.Contains("Q5 answer text.", email.HtmlBody);
    }

    [Fact]
    public void PostMeeting_GreenBadge_ContainsGreenColor()
    {
        var cp       = MakeCheckpoint(status: ReviewStatus.Green);
        var renderer = new PostMeetingReviewEmailRenderer();
        var email    = renderer.Render(MakeContext(checkpoint: cp));
        Assert.Contains("#14A085", email.HtmlBody);
    }

    [Fact]
    public void PostMeeting_RedBadge_ContainsRedColor()
    {
        var cp       = MakeCheckpoint(status: ReviewStatus.Red);
        var renderer = new PostMeetingReviewEmailRenderer();
        var email    = renderer.Render(MakeContext(checkpoint: cp));
        Assert.Contains("#E8384F", email.HtmlBody);
    }

    [Fact]
    public void PostMeeting_AmberBadge_ContainsAmberColor()
    {
        var cp       = MakeCheckpoint(status: ReviewStatus.Amber);
        var renderer = new PostMeetingReviewEmailRenderer();
        var email    = renderer.Render(MakeContext(checkpoint: cp));
        Assert.Contains("#E88600", email.HtmlBody);
    }

    [Fact]
    public void PostMeeting_ImprovingMovement_ContainsUpArrow()
    {
        var cp       = MakeCheckpoint(movement: ReviewMovement.Improving);
        var renderer = new PostMeetingReviewEmailRenderer();
        var email    = renderer.Render(MakeContext(checkpoint: cp));
        Assert.Contains("↑", email.HtmlBody);
    }

    [Fact]
    public void PostMeeting_WeakeningMovement_ContainsDownArrow()
    {
        var cp       = MakeCheckpoint(movement: ReviewMovement.Weakening);
        var renderer = new PostMeetingReviewEmailRenderer();
        var email    = renderer.Render(MakeContext(checkpoint: cp));
        Assert.Contains("↓", email.HtmlBody);
    }

    // ── ReviewEmailContextBuilder ─────────────────────────────────────────────

    [Fact]
    public void BuildContractSummary_WithContract_ReturnsMeaningfulLine()
    {
        var q2  = MakeQ2Packet(hasContracts: true);
        var str = ReviewEmailContextBuilder.BuildContractSummary(q2);
        Assert.Contains("MSA", str);
        Assert.Contains("GBP", str);
    }

    [Fact]
    public void BuildContractSummary_NoContract_ReturnsNoContractOnFile()
    {
        var q2  = MakeQ2Packet(hasContracts: false);
        var str = ReviewEmailContextBuilder.BuildContractSummary(q2);
        Assert.Equal("No contract on file", str);
    }

    [Fact]
    public void BuildProposedSummary_WithSignal_ReturnsDescription()
    {
        var q2  = MakeQ2Packet(hasSignals: true);
        var str = ReviewEmailContextBuilder.BuildProposedSummary(q2);
        Assert.Contains("Pricing", str);
    }

    [Fact]
    public void BuildProposedSummary_NoSignals_ReturnsNoPending()
    {
        var q2  = MakeQ2Packet(hasSignals: false);
        var str = ReviewEmailContextBuilder.BuildProposedSummary(q2);
        Assert.Equal("No pending commercial proposals", str);
    }

    [Fact]
    public void BuildCurrentPositionSummary_WithContract_ReturnsAnnualValueAndRenewal()
    {
        var q2  = MakeQ2Packet(hasContracts: true);
        var str = ReviewEmailContextBuilder.BuildCurrentPositionSummary(q2);
        Assert.Contains("GBP", str);
        Assert.Contains("2026-09-28", str);
    }

    [Fact]
    public void BuildCurrentPositionSummary_NoContracts_ReturnsNoContractOnFile()
    {
        var q2  = MakeQ2Packet(hasContracts: false);
        var str = ReviewEmailContextBuilder.BuildCurrentPositionSummary(q2);
        Assert.Equal("No contract on file", str);
    }
}
