using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Kozmo.Llm;

namespace Po.VendorCall;

/// <summary>
/// Composes a pre-meeting briefing email from a structured VendorCallBriefing.
///
/// Mode A (no LLM): converts the rendered deterministic briefing to HTML and returns it.
/// Mode B (with LLM): rephrases the briefing into a polished executive email.
///   — Grounding check: every [N] in the LLM body must exist in the citation list.
///   — Any failure or grounding violation falls back silently to Mode A.
/// </summary>
public interface IBriefingEmailComposer
{
    Task<BriefingEmailContent> ComposeEmailAsync(
        VendorCallBriefing briefing,
        string             renderedDeterministicBriefing,
        CancellationToken  ct = default);
}

/// <summary>Output of BriefingEmailComposer — ready for any email transport.</summary>
public sealed record BriefingEmailContent(
    string Subject,
    string HtmlBody,
    string PlainTextBody,
    bool   LlmEnhanced);

[Obsolete("Superseded by ReviewComposer + ReviewEmailRenderer — kept for reference, not called from the active pipeline")]
public sealed class BriefingEmailComposer : IBriefingEmailComposer
{
    private readonly IKozmoLlm? _llm;

    /// <param name="llm">Optional LLM for Mode B rephrase. Null = Mode A only.</param>
    public BriefingEmailComposer(IKozmoLlm? llm = null) => _llm = llm;

    // ── Entry point ───────────────────────────────────────────────────────────

    public async Task<BriefingEmailContent> ComposeEmailAsync(
        VendorCallBriefing briefing,
        string             renderedDeterministicBriefing,
        CancellationToken  ct = default)
    {
        var subject = BuildSubject(briefing);

        if (_llm is null)
            return MakeDeterministicResult(subject, renderedDeterministicBriefing);

        try
        {
            var result = await _llm.CompleteJsonAsync(
                SystemPrompt,
                renderedDeterministicBriefing,
                maxTokens: 2000,
                ct:        ct);

            if (result.Answer is JsonElement el &&
                el.TryGetProperty("body", out var bodyEl))
            {
                var body = bodyEl.GetString();
                if (!string.IsNullOrWhiteSpace(body) &&
                    PassesGroundingCheck(body, briefing.Citations))
                {
                    return new BriefingEmailContent(
                        Subject:       subject,
                        HtmlBody:      PlainToHtml(body),
                        PlainTextBody: body,
                        LlmEnhanced:  true);
                }
            }
        }
        catch
        {
            // Any failure (LlmCacheMissException, timeout, etc.) → Mode A fallback
        }

        return MakeDeterministicResult(subject, renderedDeterministicBriefing);
    }

    // ── Subject ───────────────────────────────────────────────────────────────

    private static string BuildSubject(VendorCallBriefing briefing) =>
        $"Vendor brief: {briefing.VendorName} — " +
        briefing.MeetingTime.ToString("ddd MMM dd, HH:mm", CultureInfo.InvariantCulture);

    private static BriefingEmailContent MakeDeterministicResult(string subject, string plain) =>
        new(subject, PlainToHtml(plain), plain, LlmEnhanced: false);

    // ── Grounding check ───────────────────────────────────────────────────────

    private static readonly Regex CitationPattern =
        new(@"\[(\d+)\]", RegexOptions.Compiled);

    /// <summary>
    /// Returns true iff every [N] in the LLM body maps to a real citation index.
    /// Catches hallucinated citation numbers before they reach the recipient.
    /// </summary>
    private static bool PassesGroundingCheck(
        string                          body,
        IReadOnlyList<BriefingCitation> citations)
    {
        var valid = new HashSet<int>(citations.Select(c => c.Index));
        foreach (Match m in CitationPattern.Matches(body))
        {
            if (int.TryParse(m.Groups[1].Value, out var idx) && !valid.Contains(idx))
                return false;
        }
        return true;
    }

    // ── HTML conversion ───────────────────────────────────────────────────────

    public static string PlainToHtml(string text)
    {
        var sb = new StringBuilder();
        sb.Append("<!DOCTYPE html><html><body style=\"font-family:Segoe UI,Arial,sans-serif;")
          .Append("font-size:14px;color:#222222;max-width:600px;line-height:1.6;\">");

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.TrimEnd();
            var enc  = WebUtility.HtmlEncode(line);

            if (line.Length == 0)
                sb.Append("<br>");
            else if (line.StartsWith("══") || line.StartsWith("──"))
                sb.Append("<hr style=\"border:0;border-top:1px solid #cccccc;margin:8px 0;\">");
            else if (line.StartsWith("•") || line.StartsWith("⚠"))
                sb.Append($"<div style=\"margin:2px 0 2px 20px;\">{enc}</div>");
            else if (line.TrimStart().StartsWith("[Sources:"))
                sb.Append($"<div style=\"color:#888888;font-size:12px;\">{enc}</div>");
            else if (IsAllCapsHeading(line))
                sb.Append($"<p style=\"margin:12px 0 4px 0;font-weight:bold;\">{enc}</p>");
            else
                sb.Append($"<p style=\"margin:4px 0;\">{enc}</p>");
        }

        sb.Append("</body></html>");
        return sb.ToString();
    }

    private static bool IsAllCapsHeading(string line)
    {
        if (line.Length < 3) return false;
        return line.All(c => char.IsUpper(c) || c == ' ' || c == ':' || c == '&'
                           || c == '/' || c == '–' || c == '-' || c == '—');
    }

    // ── System prompt ─────────────────────────────────────────────────────────

    private const string SystemPrompt =
        "You are composing a pre-meeting vendor briefing email for a procurement manager.\n" +
        "You will receive a structured briefing with sections, evidence, and citations.\n" +
        "Reshape it into a concise, professional email the reader can absorb in 2 minutes.\n\n" +
        "Rules:\n" +
        "- Do NOT add any information not present in the input\n" +
        "- Do NOT remove any facts\n" +
        "- Keep every source reference number [1], [2], etc. exactly as they appear in the input\n" +
        "- Do NOT invent citation numbers that were not in the input\n" +
        "- Write in clear, direct business English\n" +
        "- Lead with the most important thing going into this meeting\n" +
        "- Keep the tone professional but direct\n\n" +
        "Format the output with these exact section headings:\n" +
        "- Opening: 2-3 sentence summary of what's at stake\n" +
        "- KEY POINTS: 4-6 bullet points, each with at least one [N] citation\n" +
        "- OPEN ITEMS: overdue or outstanding commitments, one line each\n" +
        "- QUESTIONS TO RAISE: 3-5 numbered questions\n" +
        "- RECOMMENDED ACTION: one sentence\n" +
        "- SOURCES: the full citation list from the input, unchanged\n\n" +
        "Respond with JSON: {\"body\": \"<the complete email text, newlines as \\\\n>\"}";
}
