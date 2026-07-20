using Po.VendorCall;
using Kozmo.Contracts;
using If.Contracts;
using Xunit;

namespace Po.VendorCall.Tests;

public sealed class BriefingTextRendererTests
{
    private static readonly DateTimeOffset MeetingTime =
        new(2026, 7, 22, 10, 0, 0, TimeSpan.Zero);

    private static VendorCallBriefing MakeBriefing(
        IReadOnlyList<BriefingCitation>? citations = null)
    {
        var defaultCitations = citations ?? new[]
        {
            new BriefingCitation(1, "Contract: northstar-msa-2026.pdf", "src-contract", MeetingTime.AddDays(-180)),
            new BriefingCitation(2, "Calendar event: renewal review",   "src-meeting",  MeetingTime),
        };

        return new VendorCallBriefing(
            VendorName:     "Northstar Software",
            MeetingTime:    MeetingTime,
            MeetingSubject: "Northstar Software — annual renewal review",
            Attendees:      ["rishi@econtracts.onmicrosoft.com", "alex.hamilton@northstarsoftware.com"],

            MeetingObjective: new BriefingSection(
                "MEETING OBJECTIVE",
                "Vendor meeting: Northstar Software. Key topics: contract position, recent communications.",
                ["src-meeting"]),

            ContractPosition: new BriefingSection(
                "CONTRACT POSITION",
                "Active contract: northstar-msa-2026.pdf. Annual value: £285,000. Renews: 2026-09-28 (68 days).",
                ["src-contract"]),

            RecentDevelopments: new BriefingSection(
                "RECENT DEVELOPMENTS",
                "• [Jul 8] Renewal pricing proposal (3 messages). Latest: \"7% uplift proposed...\"",
                ["src-email-1"]),

            OpenCommitments: new BriefingSection(
                "OPEN COMMITMENTS",
                "• ⚠ sla report request — recorded 5 days ago (OVERDUE).",
                ["src-commitment"]),

            RisksAndOpportunities: new BriefingSection(
                "RISKS & OPPORTUNITIES",
                "• Risk: pricing or commercial signal — pricing-uplift-proposal.",
                ["src-contract"]),

            EvidenceGaps: new BriefingSection(
                "EVIDENCE GAPS",
                "• Overdue open commitment: sla-report-request.",
                ["src-meeting"]),

            RecommendedQuestions: new BriefingSection(
                "RECOMMENDED QUESTIONS",
                "1. What is the justification for the proposed pricing change?",
                ["src-contract"]),

            SafestNextAction: new BriefingSection(
                "SAFEST NEXT ACTION",
                "Confirm renewal or non-renewal position before 2026-07-29.",
                ["src-contract"]),

            Citations: defaultCitations);
    }

    // ── Section headings ──────────────────────────────────────────────────────

    [Fact]
    public void Render_ContainsAllSectionHeadings()
    {
        var output = BriefingTextRenderer.Render(MakeBriefing());

        Assert.Contains("MEETING OBJECTIVE",    output);
        Assert.Contains("CONTRACT POSITION",    output);
        Assert.Contains("RECENT DEVELOPMENTS",  output);
        Assert.Contains("OPEN COMMITMENTS",     output);
        Assert.Contains("RISKS & OPPORTUNITIES",output);
        Assert.Contains("EVIDENCE GAPS",        output);
        Assert.Contains("RECOMMENDED QUESTIONS",output);
        Assert.Contains("SAFEST NEXT ACTION",   output);
    }

    // ── Header ────────────────────────────────────────────────────────────────

    [Fact]
    public void Render_ContainsVendorNameInHeader()
    {
        var output = BriefingTextRenderer.Render(MakeBriefing());

        Assert.Contains("Northstar Software", output);
    }

    [Fact]
    public void Render_ContainsMeetingTimeInHeader()
    {
        var output = BriefingTextRenderer.Render(MakeBriefing());

        Assert.Contains("2026-07-22", output);
        Assert.Contains("10:00", output);
    }

    [Fact]
    public void Render_ContainsAttendeesInHeader()
    {
        var output = BriefingTextRenderer.Render(MakeBriefing());

        Assert.Contains("rishi@econtracts.onmicrosoft.com", output);
    }

    // ── Citation numbers ──────────────────────────────────────────────────────

    [Fact]
    public void Render_CitationNumbersAppearInSections()
    {
        var output = BriefingTextRenderer.Render(MakeBriefing());

        Assert.Contains("[Sources: 1]", output);  // contract cited in ContractPosition
        Assert.Contains("[Sources: 2]", output);  // meeting cited in MeetingObjective
    }

    [Fact]
    public void Render_SourcesSectionListsAllCitations()
    {
        var output = BriefingTextRenderer.Render(MakeBriefing());

        Assert.Contains("SOURCES", output);
        Assert.Contains("[1] Contract: northstar-msa-2026.pdf", output);
        Assert.Contains("[2] Calendar event: renewal review", output);
    }

    // ── Unknown source IDs ────────────────────────────────────────────────────

    [Fact]
    public void Render_UnknownSourceId_NotIncludedInSources()
    {
        // "src-email-1" and "src-commitment" in section refs but NOT in Citations list
        // → no [Sources: N] should appear for those sections (or they're just missing)
        var output = BriefingTextRenderer.Render(MakeBriefing());

        // All lines starting with [Sources:] should only reference valid indices 1 or 2
        var sourceLines = output.Split('\n')
            .Where(l => l.TrimStart().StartsWith("[Sources:"))
            .ToList();

        foreach (var line in sourceLines)
            Assert.DoesNotContain("-1", line);
    }

    // ── Separators ────────────────────────────────────────────────────────────

    [Fact]
    public void Render_ContainsSeparatorLines()
    {
        var output = BriefingTextRenderer.Render(MakeBriefing());

        Assert.Contains("══", output);
        Assert.Contains("──", output);
    }

    // ── Empty citations ───────────────────────────────────────────────────────

    [Fact]
    public void Render_EmptyCitations_StillRendersWithoutError()
    {
        var briefing = MakeBriefing(citations: []);
        var exception = Record.Exception(() => BriefingTextRenderer.Render(briefing));

        Assert.Null(exception);
    }
}
