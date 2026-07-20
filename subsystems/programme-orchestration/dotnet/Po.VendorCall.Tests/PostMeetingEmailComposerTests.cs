#pragma warning disable CS0618 // obsolete pre-Review-pipeline composers kept for reference
using System.Text.Json;
using Kozmo.Llm;
using Po.VendorCall;
using Xunit;

namespace Po.VendorCall.Tests;

public sealed class PostMeetingEmailComposerTests
{
    // ── Fixtures ──────────────────────────────────────────────────────────────

    private static PostMeetingSummary MakeSummary() =>
        new(
            VendorName:    "Northstar Software",
            MeetingTime:   new DateTimeOffset(2026, 7, 22, 10, 0, 0, TimeSpan.Zero),
            MeetingSubject: "Northstar Software — annual renewal review",
            Attendees:     [],
            MeetingOutcome:        new SummarySection("MEETING OUTCOME",        "3 of 5 items addressed.", [], ["pre-meeting-brief"]),
            DecisionsMade:         new SummarySection("DECISIONS MADE",         "1 decision.",             [], ["00:59"]),
            NewCommitments:        new SummarySection("NEW COMMITMENTS",         "Vendor: 3, Yours: 1.",    [], ["01:19", "01:41"]),
            ResolvedFromPreBrief:  new SummarySection("RESOLVED FROM PRE-BRIEF","3 of 5 addressed.",       [], ["pre-meeting-brief"]),
            CommercialStateChange: new SummarySection("COMMERCIAL STATE CHANGE", "Stable.",                 [], ["pre-meeting-brief"]),
            StillOpen:             new SummarySection("STILL OPEN",             "2 remain.",               [], ["pre-meeting-brief"]),
            RecommendedNextAction: new SummarySection("RECOMMENDED NEXT ACTION","Follow up Friday.",       [], ["01:19"]),
            Citations:
            [
                new SummaryCitation(1, "Pre-meeting brief: Northstar Software", null,    new DateTimeOffset(2026, 7, 22, 9, 0, 0, TimeSpan.Zero)),
                new SummaryCitation(2, "Transcript: Pricing deferred",          "00:59",  null),
                new SummaryCitation(3, "Transcript: Pricing breakdown Friday",  "01:19",  null),
                new SummaryCitation(4, "Transcript: SLA report commitment",     "01:41",  null),
            ]);

    private const string SampleRendered =
        "POST-MEETING SUMMARY: Northstar Software\n\n" +
        "MEETING OUTCOME\n3 of 5 items were addressed. [T:01:19]\n\n" +
        "SOURCES\n[Pre] Pre-meeting brief\n[T:01:19] Transcript: pricing breakdown";

    private const string ReviewUrl =
        "http://localhost:5050/vendor-calls/abc123/review?token=test-token";

    // ── Mode A (no LLM) ───────────────────────────────────────────────────────

    [Fact]
    public async Task LlmNull_Returns_LlmEnhancedFalse()
    {
        var result = await new PostMeetingEmailComposer(llm: null)
            .ComposeEmailAsync(MakeSummary(), SampleRendered, ReviewUrl);

        Assert.False(result.LlmEnhanced);
    }

    [Fact]
    public async Task LlmNull_PlainTextBody_ContainsDeterministicContent()
    {
        var result = await new PostMeetingEmailComposer(llm: null)
            .ComposeEmailAsync(MakeSummary(), SampleRendered, ReviewUrl);

        Assert.Contains("POST-MEETING SUMMARY", result.PlainTextBody);
    }

    // ── Review link ───────────────────────────────────────────────────────────

    [Fact]
    public async Task ReviewLink_AppearsInPlainTextBody()
    {
        var result = await new PostMeetingEmailComposer(llm: null)
            .ComposeEmailAsync(MakeSummary(), SampleRendered, ReviewUrl);

        Assert.Contains(ReviewUrl, result.PlainTextBody);
    }

    [Fact]
    public async Task ReviewLink_AppearsInHtmlBody()
    {
        var result = await new PostMeetingEmailComposer(llm: null)
            .ComposeEmailAsync(MakeSummary(), SampleRendered, ReviewUrl);

        Assert.Contains("localhost:5050", result.HtmlBody);
    }

    // ── Subject line ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Subject_MatchesExpectedFormat()
    {
        var result = await new PostMeetingEmailComposer(llm: null)
            .ComposeEmailAsync(MakeSummary(), SampleRendered, ReviewUrl);

        // 2026-07-22 is a Wednesday
        Assert.Equal("Meeting summary: Northstar Software — Wed Jul 22", result.Subject);
    }

    // ── Mode B (LLM available) ────────────────────────────────────────────────

    [Fact]
    public async Task LlmSucceeds_ValidTimestamps_Returns_LlmEnhancedTrue()
    {
        // [T:01:19] is a valid citation timestamp
        var result = await new PostMeetingEmailComposer(llm: new PostMeetingSucceedingLlm("[T:01:19]"))
            .ComposeEmailAsync(MakeSummary(), SampleRendered, ReviewUrl);

        Assert.True(result.LlmEnhanced);
    }

    [Fact]
    public async Task LlmFails_FallsBackToDeterministic()
    {
        var result = await new PostMeetingEmailComposer(llm: new PostMeetingThrowingLlm())
            .ComposeEmailAsync(MakeSummary(), SampleRendered, ReviewUrl);

        Assert.False(result.LlmEnhanced);
        Assert.NotEmpty(result.PlainTextBody);
    }

    // ── Grounding check ───────────────────────────────────────────────────────

    [Fact]
    public async Task GroundingCheck_HallucinatedTimestamp_FallsBackToDeterministic()
    {
        // [T:99:99] is not in any citation
        var result = await new PostMeetingEmailComposer(llm: new PostMeetingSucceedingLlm("[T:99:99]"))
            .ComposeEmailAsync(MakeSummary(), SampleRendered, ReviewUrl);

        Assert.False(result.LlmEnhanced);
    }

    [Fact]
    public async Task GroundingCheck_NoTimestampsInLlmOutput_Passes()
    {
        // LLM output with no [T:xx:xx] markers passes grounding check
        var result = await new PostMeetingEmailComposer(llm: new PostMeetingSucceedingLlm())
            .ComposeEmailAsync(MakeSummary(), SampleRendered, ReviewUrl);

        Assert.True(result.LlmEnhanced);
    }

    // ── Review token ──────────────────────────────────────────────────────────

    [Fact]
    public void ReviewToken_IsGenerated_NonEmpty()
    {
        var token = ReviewTokenGenerator.Generate();
        Assert.NotEmpty(token);
    }

    [Fact]
    public void ReviewToken_HasSufficientLength()
    {
        // 32 bytes → 43 base64url chars (no padding)
        var token = ReviewTokenGenerator.Generate();
        Assert.True(token.Length >= 40, $"Token too short: {token.Length} chars");
    }

    [Fact]
    public void ReviewToken_ExpiresAt_IsApproximately48HoursInFuture()
    {
        var origin = DateTimeOffset.UtcNow;
        var expiry = ReviewTokenGenerator.ExpiresAt(origin);

        Assert.True(expiry > origin.AddHours(47), "Expiry should be ~48h in future");
        Assert.True(expiry < origin.AddHours(49), "Expiry should not be more than 49h in future");
    }
}

// ── Test doubles ──────────────────────────────────────────────────────────────

internal sealed class PostMeetingSucceedingLlm : IKozmoLlm
{
    private readonly string _extraContent;

    public PostMeetingSucceedingLlm(params string[] extras)
        => _extraContent = string.Join(" ", extras);

    public Task<LlmResult> CompleteJsonAsync(
        string system, string user, int maxTokens = 500, CancellationToken ct = default)
    {
        var body   = $"LLM-ENHANCED post-meeting summary {_extraContent}.".Replace("\"", "'");
        var json   = $"{{\"body\": \"{body}\"}}";
        var result = new LlmResult(JsonDocument.Parse(json).RootElement, 0.95, "stub");
        return Task.FromResult(result);
    }
}

internal sealed class PostMeetingThrowingLlm : IKozmoLlm
{
    public Task<LlmResult> CompleteJsonAsync(
        string system, string user, int maxTokens = 500, CancellationToken ct = default)
        => throw new InvalidOperationException("LLM unavailable");
}
