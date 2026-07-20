using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Kozmo.Llm;

namespace Po.VendorCall;

/// <summary>
/// Composes a post-meeting summary email from a PostMeetingSummary + review URL.
///
/// Mode A (no LLM): converts the rendered deterministic summary to HTML and returns it.
/// Mode B (with LLM): rephrases into a polished executive email.
///   — Grounding check: every [T:xx:xx] in the LLM body must match a real citation.
///   — Any failure or grounding violation falls back silently to Mode A.
/// The review link footer is always appended regardless of mode.
/// </summary>
public interface IPostMeetingEmailComposer
{
    Task<PostMeetingEmailContent> ComposeEmailAsync(
        PostMeetingSummary summary,
        string             renderedDeterministicSummary,
        string             reviewUrl,
        CancellationToken  ct = default);
}

/// <summary>Output of PostMeetingEmailComposer — ready for any email transport.</summary>
public sealed record PostMeetingEmailContent(
    string Subject,
    string HtmlBody,
    string PlainTextBody,
    bool   LlmEnhanced);

[Obsolete("Superseded by ReviewComposer + ReviewEmailRenderer — kept for reference, not called from the active pipeline")]
public sealed class PostMeetingEmailComposer : IPostMeetingEmailComposer
{
    private readonly IKozmoLlm? _llm;

    /// <param name="llm">Optional LLM for Mode B rephrase. Null = Mode A only.</param>
    public PostMeetingEmailComposer(IKozmoLlm? llm = null) => _llm = llm;

    // ── Entry point ───────────────────────────────────────────────────────────

    public async Task<PostMeetingEmailContent> ComposeEmailAsync(
        PostMeetingSummary summary,
        string             renderedDeterministicSummary,
        string             reviewUrl,
        CancellationToken  ct = default)
    {
        var subject      = BuildSubject(summary);
        var reviewFooter = BuildReviewFooter(reviewUrl);

        if (_llm is null)
            return MakeDeterministicResult(subject, renderedDeterministicSummary + reviewFooter);

        try
        {
            var result = await _llm.CompleteJsonAsync(
                BuildSystemPrompt(reviewUrl),
                renderedDeterministicSummary,
                maxTokens: 2000,
                ct:        ct);

            if (result.Answer is JsonElement el &&
                el.TryGetProperty("body", out var bodyEl))
            {
                var body = bodyEl.GetString();
                if (!string.IsNullOrWhiteSpace(body) &&
                    PassesGroundingCheck(body, summary.Citations))
                {
                    var full = body + reviewFooter;
                    return new PostMeetingEmailContent(
                        Subject:       subject,
                        HtmlBody:      BriefingEmailComposer.PlainToHtml(full),
                        PlainTextBody: full,
                        LlmEnhanced:  true);
                }
            }
        }
        catch
        {
            // Any failure (LlmCacheMissException, timeout, etc.) → Mode A fallback
        }

        return MakeDeterministicResult(subject, renderedDeterministicSummary + reviewFooter);
    }

    // ── Subject ───────────────────────────────────────────────────────────────

    private static string BuildSubject(PostMeetingSummary summary) =>
        $"Meeting summary: {summary.VendorName} — " +
        summary.MeetingTime.ToString("ddd MMM dd", CultureInfo.InvariantCulture);

    private static PostMeetingEmailContent MakeDeterministicResult(string subject, string plain) =>
        new(subject, BriefingEmailComposer.PlainToHtml(plain), plain, LlmEnhanced: false);

    // ── Review link footer ────────────────────────────────────────────────────

    internal static string BuildReviewFooter(string reviewUrl) =>
        $"""


        ────────────────────────────────
        ⚡ Review and confirm this summary:
           {reviewUrl}

        This summary was generated from the meeting transcript.
        Please review before it becomes part of the commercial record.
        Items marked ⚠ require your confirmation.
        ────────────────────────────────
        """;

    // ── Grounding check ───────────────────────────────────────────────────────

    private static readonly Regex TimestampPattern =
        new(@"\[T:(\d{1,2}:\d{2})\]", RegexOptions.Compiled);

    /// <summary>
    /// Returns true iff every [T:xx:xx] in the LLM body matches a real citation timestamp.
    /// Catches hallucinated timestamps before they reach the recipient.
    /// </summary>
    private static bool PassesGroundingCheck(
        string                         body,
        IReadOnlyList<SummaryCitation>  citations)
    {
        var validTs = new HashSet<string>(
            citations
                .Where(c => c.TranscriptTimestamp != null)
                .Select(c => c.TranscriptTimestamp!),
            StringComparer.OrdinalIgnoreCase);

        foreach (Match m in TimestampPattern.Matches(body))
        {
            if (!validTs.Contains(m.Groups[1].Value))
                return false;
        }
        return true;
    }

    // ── LLM system prompt ─────────────────────────────────────────────────────

    private static string BuildSystemPrompt(string reviewUrl) =>
        "You are composing a post-meeting vendor summary email for a procurement manager.\n" +
        "You will receive a structured summary with sections, evidence, and transcript references.\n" +
        "Reshape it into a concise, professional email readable in 2 minutes.\n\n" +
        "Rules:\n" +
        "- Do NOT add any information not present in the input\n" +
        "- Do NOT remove any facts\n" +
        "- Keep every transcript reference [T:xx:xx] exactly as it appears in the input\n" +
        "- Do NOT invent timestamps that were not in the input\n" +
        "- Items marked ⚠ must keep the ⚠ marker\n" +
        "- Lead with the most important outcome\n" +
        "- Tone: direct, professional, action-oriented\n\n" +
        "Format the output with these exact section headings:\n" +
        "- Opening: 2-3 sentences (what happened vs what was expected)\n" +
        "- DECISIONS & COMMITMENTS: combined, bulleted, with owners and dates\n" +
        "- RESOLVED / STILL OPEN: brief status of pre-meeting items\n" +
        "- RECOMMENDED ACTION: one sentence\n" +
        "- SOURCES: full citation list from the input, unchanged\n\n" +
        "Respond with JSON: {\"body\": \"<the complete email text, newlines as \\\\n>\"}";
}
