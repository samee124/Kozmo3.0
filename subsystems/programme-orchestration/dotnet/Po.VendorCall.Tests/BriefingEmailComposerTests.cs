#pragma warning disable CS0618 // obsolete pre-Review-pipeline composers kept for reference
using System.Text.Json;
using Kozmo.Contracts;
using Kozmo.Llm;
using Po.VendorCall;
using Xunit;

namespace Po.VendorCall.Tests;

public sealed class BriefingEmailComposerTests
{
    // ── Fixtures ──────────────────────────────────────────────────────────────

    private static readonly DateTimeOffset MeetingTime =
        new(2026, 7, 22, 10, 0, 0, TimeSpan.Zero);

    private static VendorCallBriefing MakeBriefing(
        IReadOnlyList<BriefingCitation>? citations = null)
    {
        var cites = citations ?? new[]
        {
            new BriefingCitation(1, "Calendar event: renewal review",         "src-meeting",  MeetingTime),
            new BriefingCitation(2, "Signed contract: northstar-msa-2026.pdf","src-contract", MeetingTime.AddDays(-180)),
        };

        return new VendorCallBriefing(
            VendorName:    "Northstar Software",
            MeetingTime:   MeetingTime,
            MeetingSubject: "Northstar Software — annual renewal review",
            Attendees:     ["rishi@econtracts.onmicrosoft.com"],
            MeetingObjective:      new BriefingSection("MEETING OBJECTIVE",     "Vendor meeting.", ["src-meeting"]),
            ContractPosition:      new BriefingSection("CONTRACT POSITION",     "Active contract: northstar-msa-2026.pdf. Annual value: £285,000.", ["src-contract"]),
            RecentDevelopments:    new BriefingSection("RECENT DEVELOPMENTS",   "No recent emails.", ["src-meeting"]),
            OpenCommitments:       new BriefingSection("OPEN COMMITMENTS",      "No commitments.", ["src-meeting"]),
            RisksAndOpportunities: new BriefingSection("RISKS & OPPORTUNITIES", "No risks.", ["src-contract"]),
            EvidenceGaps:          new BriefingSection("EVIDENCE GAPS",         "No gaps.", ["src-meeting"]),
            RecommendedQuestions:  new BriefingSection("RECOMMENDED QUESTIONS", "1. Key question?", ["src-contract"]),
            SafestNextAction:      new BriefingSection("SAFEST NEXT ACTION",    "Review open items.", ["src-meeting"]),
            Citations:             cites);
    }

    private const string SampleRendered =
        "PRE-MEETING BRIEF: Northstar Software\n\n" +
        "CONTRACT POSITION\nActive contract [1].\n\n" +
        "SOURCES\n[1] Calendar event (2026-07-22)\n[2] Signed contract (2026-01-20)";

    // ── Mode A (no LLM) ───────────────────────────────────────────────────────

    [Fact]
    public async Task LlmNull_Returns_LlmEnhancedFalse()
    {
        var result = await new BriefingEmailComposer(llm: null)
            .ComposeEmailAsync(MakeBriefing(), SampleRendered);

        Assert.False(result.LlmEnhanced);
    }

    [Fact]
    public async Task LlmNull_PlainTextBody_EqualsDeterministicRendering()
    {
        var result = await new BriefingEmailComposer(llm: null)
            .ComposeEmailAsync(MakeBriefing(), SampleRendered);

        Assert.Equal(SampleRendered, result.PlainTextBody);
    }

    // ── Mode B (LLM available) ────────────────────────────────────────────────

    [Fact]
    public async Task LlmSucceeds_ValidCitations_Returns_LlmEnhancedTrue()
    {
        // LLM returns body referencing only [1] and [2] which exist
        var result = await new BriefingEmailComposer(llm: new SucceedingLlm("[1]", "[2]"))
            .ComposeEmailAsync(MakeBriefing(), SampleRendered);

        Assert.True(result.LlmEnhanced);
    }

    [Fact]
    public async Task LlmSucceeds_PlainTextBody_ContainsLlmOutput()
    {
        var result = await new BriefingEmailComposer(llm: new SucceedingLlm("[1]", "[2]"))
            .ComposeEmailAsync(MakeBriefing(), SampleRendered);

        Assert.Contains("LLM-ENHANCED", result.PlainTextBody);
    }

    [Fact]
    public async Task LlmFails_FallsBack_LlmEnhancedFalse()
    {
        var result = await new BriefingEmailComposer(llm: new ThrowingLlm())
            .ComposeEmailAsync(MakeBriefing(), SampleRendered);

        Assert.False(result.LlmEnhanced);
        Assert.NotEmpty(result.PlainTextBody);
    }

    // ── Grounding check ───────────────────────────────────────────────────────

    [Fact]
    public async Task LlmHallucinatedCitation_FallsBackToDeterministic()
    {
        // LLM returns [99] which is not in Citations (only [1] and [2] exist)
        var result = await new BriefingEmailComposer(llm: new SucceedingLlm("[1]", "[99]"))
            .ComposeEmailAsync(MakeBriefing(), SampleRendered);

        Assert.False(result.LlmEnhanced);
    }

    [Fact]
    public async Task LlmNoCitationsInBody_PassesGroundingCheck()
    {
        // LLM returns content with no [N] markers at all — grounding check passes (nothing to invalidate)
        var result = await new BriefingEmailComposer(llm: new SucceedingLlm())
            .ComposeEmailAsync(MakeBriefing(), SampleRendered);

        Assert.True(result.LlmEnhanced);
    }

    [Fact]
    public async Task LlmValidCitationAtBoundary_Passes()
    {
        // [2] is the highest citation in our two-citation briefing — must not be rejected
        var result = await new BriefingEmailComposer(llm: new SucceedingLlm("[2]"))
            .ComposeEmailAsync(MakeBriefing(), SampleRendered);

        Assert.True(result.LlmEnhanced);
    }

    // ── Subject line ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Subject_MatchesExpectedFormat()
    {
        var result = await new BriefingEmailComposer(llm: null)
            .ComposeEmailAsync(MakeBriefing(), SampleRendered);

        Assert.Equal("Vendor brief: Northstar Software — Wed Jul 22, 10:00", result.Subject);
    }

    [Fact]
    public async Task Subject_IsNotEmpty_WhenLlmNull()
    {
        var result = await new BriefingEmailComposer(llm: null)
            .ComposeEmailAsync(MakeBriefing(), SampleRendered);

        Assert.NotEmpty(result.Subject);
    }

    // ── HTML body ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task HtmlBody_ContainsHtmlTags()
    {
        var result = await new BriefingEmailComposer(llm: null)
            .ComposeEmailAsync(MakeBriefing(), SampleRendered);

        Assert.Contains("<html>", result.HtmlBody);
        Assert.Contains("</html>", result.HtmlBody);
        Assert.Contains("<body", result.HtmlBody);
    }

    [Fact]
    public async Task HtmlBody_NeverEmpty()
    {
        var result = await new BriefingEmailComposer(llm: null)
            .ComposeEmailAsync(MakeBriefing(), SampleRendered);

        Assert.NotEmpty(result.HtmlBody);
    }

    // ── PlainToHtml unit tests ────────────────────────────────────────────────

    [Fact]
    public void PlainToHtml_SeparatorLines_BecomeHr()
    {
        var html = BriefingEmailComposer.PlainToHtml("══════\nsome text\n──────");

        Assert.Contains("<hr", html);
    }

    [Fact]
    public void PlainToHtml_BulletLines_GetIndentStyle()
    {
        var html = BriefingEmailComposer.PlainToHtml("• A bullet point here");

        Assert.Contains("20px", html);   // left-margin indent via shorthand margin
        Assert.Contains("bullet point", html);
    }

    [Fact]
    public void PlainToHtml_AllCapsLine_GetsBoldStyle()
    {
        var html = BriefingEmailComposer.PlainToHtml("CONTRACT POSITION");

        Assert.Contains("font-weight:bold", html);
        Assert.Contains("CONTRACT POSITION", html);
    }

    [Fact]
    public void PlainToHtml_EmptyInput_ReturnsValidHtml()
    {
        var html = BriefingEmailComposer.PlainToHtml("");

        Assert.Contains("<html>", html);
        Assert.Contains("</html>", html);
    }
}

// ── Test doubles ───────────────────────────────────────────────────────────────

/// <summary>
/// Returns a valid JSON body containing the specified citation markers.
/// Used to test the grounding check with both valid and hallucinated citations.
/// </summary>
internal sealed class SucceedingLlm : IKozmoLlm
{
    private readonly string _extraCitations;

    public SucceedingLlm(params string[] citations)
        => _extraCitations = string.Join(" ", citations);

    public Task<LlmResult> CompleteJsonAsync(
        string system, string user, int maxTokens = 500, CancellationToken ct = default)
    {
        var body   = $"LLM-ENHANCED email content {_extraCitations}.".Replace("\"", "'");
        var json   = $"{{\"body\": \"{body}\"}}";
        var result = new LlmResult(JsonDocument.Parse(json).RootElement, 0.95, "stub");
        return Task.FromResult(result);
    }

    public Task<LlmResult> CompleteVisionAsync(
        string system, string user, string imageBase64, string mimeType,
        int maxTokens = 500, CancellationToken ct = default)
        => throw new NotSupportedException();
}
