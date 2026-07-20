using System.Net;
using System.Text;

namespace Po.VendorCall;

// ==========================================================
// Shared context record + RenderedEmail output
// ==========================================================

/// <summary>
/// All data needed to render either a pre-meeting or post-meeting review email.
/// Build this from a ReviewCompositionResult and a VendorCallRun.
/// </summary>
public sealed record ReviewEmailContext(
    string           VendorName,
    /// <summary>e.g. "Northstar MSA 2026 — GBP 285,000/yr"</summary>
    string           ContractSummary,
    DateTimeOffset   MeetingTimeUtc,
    /// <summary>Null for first-ever review.</summary>
    DateTimeOffset?  PreviousReviewDateUtc,
    /// <summary>e.g. "Pre-renewal, notice window open"</summary>
    string           RenewalStagePhrase,
    /// <summary>Short phrase from Q2 commercial signals e.g. "Pricing uplift signal outstanding".</summary>
    string           ProposedSummary,
    /// <summary>Short phrase from Q2 contract facts e.g. "GBP 285,000/yr · renewal Sep 28".</summary>
    string           CurrentPositionSummary,
    ReviewCheckpoint Checkpoint,
    string           ViewEvidenceUrl,
    string           PostUpdateUrl,
    string           FlagUrl);

/// <summary>Rendered output — both HTML (inline-styled, email-safe) and plain text.</summary>
public sealed record RenderedEmail(string Subject, string HtmlBody, string PlainTextBody);

// ==========================================================
// Static helpers — derive summary phrases from Q2 packet
// ==========================================================

public static class ReviewEmailContextBuilder
{
    /// <summary>
    /// Derives a short "current position" phrase from a Q2 fact packet's contract facts.
    /// Use this to build ReviewEmailContext.CurrentPositionSummary.
    /// </summary>
    public static string BuildCurrentPositionSummary(Q2FactPacket q2)
    {
        if (q2.Contracts.Count == 0) return "No contract on file";
        var c     = q2.Contracts[0];
        var parts = new List<string> { c.Type };
        if (c.AnnualValue.HasValue)
            parts.Add($"{c.Currency ?? "GBP"} {c.AnnualValue:N0}/yr");
        if (!string.IsNullOrEmpty(c.RenewalDate))
            parts.Add($"renewal {c.RenewalDate}");
        return string.Join(" · ", parts);
    }

    /// <summary>
    /// Derives a short "proposed / at-risk" phrase from Q2 commercial signals.
    /// Use this to build ReviewEmailContext.ProposedSummary.
    /// </summary>
    public static string BuildProposedSummary(Q2FactPacket q2)
    {
        if (q2.CommercialSignals.Count == 0)
            return "No pending commercial proposals";
        var first = q2.CommercialSignals[0];
        return q2.CommercialSignals.Count == 1
            ? first.Description
            : $"{q2.CommercialSignals.Count} signals: {first.Description}";
    }

    /// <summary>
    /// Builds a contract-summary line from Q2 fact packet for the facts table.
    /// e.g. "Northstar MSA 2026 — GBP 285,000/yr" or "No contract on file".
    /// </summary>
    public static string BuildContractSummary(Q2FactPacket q2)
    {
        if (q2.Contracts.Count == 0) return "No contract on file";
        var c     = q2.Contracts[0];
        var parts = new List<string>();
        if (c.AnnualValue.HasValue)
            parts.Add($"{c.Currency ?? "GBP"} {c.AnnualValue:N0}/yr");
        if (!string.IsNullOrEmpty(c.RenewalDate))
            parts.Add($"renews {c.RenewalDate}");
        return parts.Count > 0 ? $"{c.Type} — {string.Join(", ", parts)}" : c.Type;
    }
}

// ==========================================================
// Pre-meeting renderer
// ==========================================================

public interface IPreMeetingReviewEmailRenderer
{
    RenderedEmail Render(ReviewEmailContext context, int minutesUntilMeeting);
}

/// <summary>
/// Renders the pre-meeting review email — full layout (overview header, proposed/current
/// comparison, Q1–Q5 boxes, buttons, flag line) as inline-styled HTML + plain text.
/// </summary>
public sealed class PreMeetingReviewEmailRenderer : IPreMeetingReviewEmailRenderer
{
    public RenderedEmail Render(ReviewEmailContext ctx, int minutesUntilMeeting)
    {
        var subject   = $"{ctx.VendorName} Pre-Meeting Brief";
        var title     = $"{ctx.VendorName} — Pre-Meeting Brief";
        var flagLine  = "Something look off? Flag this brief";
        var updateBtn = "Post an update";

        return new RenderedEmail(
            Subject:      subject,
            HtmlBody:     ReviewEmailRenderHelper.RenderHtml(ctx, title, flagLine, updateBtn),
            PlainTextBody: ReviewEmailRenderHelper.RenderPlainText(ctx, subject, flagLine, updateBtn));
    }
}

// ==========================================================
// Post-meeting renderer
// ==========================================================

public interface IPostMeetingReviewEmailRenderer
{
    RenderedEmail Render(ReviewEmailContext context);
}

/// <summary>
/// Identical layout to the pre-meeting renderer with two differences:
/// header title says "meeting summary"; flag line reads "Review and confirm this summary".
/// </summary>
public sealed class PostMeetingReviewEmailRenderer : IPostMeetingReviewEmailRenderer
{
    public RenderedEmail Render(ReviewEmailContext ctx)
    {
        var subject   = $"{ctx.VendorName} Post-Meeting Brief";
        var title     = $"{ctx.VendorName} — Post-Meeting Brief";
        var flagLine  = "Review and confirm this summary";
        var updateBtn = "Post an update";

        return new RenderedEmail(
            Subject:      subject,
            HtmlBody:     ReviewEmailRenderHelper.RenderHtml(ctx, title, flagLine, updateBtn),
            PlainTextBody: ReviewEmailRenderHelper.RenderPlainText(ctx, subject, flagLine, updateBtn));
    }
}

// ==========================================================
// Shared HTML + plain-text rendering (file-scope)
// ==========================================================

file static class ReviewEmailRenderHelper
{
    // Badge palette — all inline hex, no CSS variables (email-safe)
    private static (string bg, string text) BadgeColor(ReviewStatus s) => s switch
    {
        ReviewStatus.Green => ("#14A085", "#FFFFFF"),
        ReviewStatus.Red   => ("#E8384F", "#FFFFFF"),
        _                  => ("#E88600", "#FFFFFF"),  // Amber
    };

    private static string MovementArrow(ReviewMovement m) => m switch
    {
        ReviewMovement.Improving => "↑",
        ReviewMovement.Weakening => "↓",
        _                        => "→",
    };

    internal static string RenderHtml(
        ReviewEmailContext ctx, string title, string flagLine, string updateBtnLabel)
    {
        var cp  = ctx.Checkpoint;
        var (badgeBg, badgeText) = BadgeColor(cp.Status);
        var arrow       = MovementArrow(cp.Movement);
        var prevDate    = ctx.PreviousReviewDateUtc.HasValue
            ? ctx.PreviousReviewDateUtc.Value.ToString("d MMM yyyy")
            : "First tracked review";
        var meetingDate = ctx.MeetingTimeUtc.ToString("ddd d MMM yyyy, HH:mm") + " UTC";

        var sb = new StringBuilder();
        sb.Append($"""
            <!DOCTYPE html>
            <html lang="en">
            <head>
              <meta charset="utf-8">
              <meta name="viewport" content="width=device-width,initial-scale=1">
              <title>Kozmo — {WebUtility.HtmlEncode(title)}</title>
            </head>
            <body style="margin:0;padding:0;background:#F9F9FA;font-family:system-ui,-apple-system,'Segoe UI',Arial,sans-serif;">
            <table width="100%" cellpadding="0" cellspacing="0" border="0" style="background:#F9F9FA;">
            <tr><td align="center" style="padding:32px 16px;">
            <table width="600" cellpadding="0" cellspacing="0" border="0" style="max-width:600px;width:100%;">

            <!-- Logo -->
            <tr><td style="padding-bottom:24px;">
              <table cellpadding="0" cellspacing="0" border="0"><tr>
                <td style="width:28px;height:28px;background:#A9762F;border-radius:50%;text-align:center;vertical-align:middle;">
                  <span style="font-size:11px;font-weight:700;color:#fff;line-height:28px;">K</span>
                </td>
                <td style="padding-left:8px;font-size:15px;font-weight:700;color:#1E1F21;vertical-align:middle;">Kozmo</td>
              </tr></table>
            </td></tr>

            <!-- Overview header card -->
            <tr><td style="background:#fff;border:1px solid #EDEAE9;border-radius:12px;padding:24px;margin-bottom:16px;">

              <h2 style="font-size:17px;font-weight:700;color:#1E1F21;margin:0 0 20px;">{WebUtility.HtmlEncode(title)}</h2>

              <!-- Facts table -->
              <table width="100%" cellpadding="0" cellspacing="0" border="0" style="border-collapse:collapse;">
            """);

        void FactRow(string label, string value)
        {
            sb.Append($"""
                <tr>
                  <td style="font-size:12px;color:#6D6E72;padding:5px 0;width:38%;vertical-align:top;">{WebUtility.HtmlEncode(label)}</td>
                  <td style="font-size:13px;color:#1E1F21;padding:5px 0;font-weight:500;vertical-align:top;">{WebUtility.HtmlEncode(value)}</td>
                </tr>
                """);
        }

        FactRow("Vendor",          ctx.VendorName);
        FactRow("Contract",        ctx.ContractSummary);
        FactRow("Meeting",         meetingDate);
        FactRow("Previous review", prevDate);
        FactRow("Renewal stage",   ctx.RenewalStagePhrase);

        sb.Append($"""
              </table>

              <!-- Status badge row -->
              <div style="margin-top:18px;padding-top:16px;border-top:1px solid #E5E5E7;">
                <span style="display:inline-block;background:{badgeBg};color:{badgeText};padding:3px 12px;border-radius:999px;font-size:12px;font-weight:700;">{cp.Status}</span>
                <span style="display:inline-block;margin-left:10px;font-size:13px;color:#54545A;">{arrow} {cp.Movement}</span>
                <span style="display:inline-block;margin-left:10px;font-size:12px;color:#9C9CA0;">{cp.Confidence} confidence</span>
              </div>

              <!-- Overview narrative -->
              <p style="margin:16px 0 0;font-size:13px;color:#54545A;line-height:1.65;">{WebUtility.HtmlEncode(ctx.Checkpoint.Q1Answer.Length > 0 ? ctx.Checkpoint.Q1Answer : "")}</p>
              {(!string.IsNullOrEmpty(ctx.Checkpoint.Q1Answer) && ctx.Checkpoint.Q1Answer != ctx.Checkpoint.Q2Answer
                  ? $"<p style=\"margin:10px 0 0;font-size:13px;color:#54545A;line-height:1.65;\">{WebUtility.HtmlEncode(ctx.Checkpoint.Q2Answer)}</p>"
                  : "")}

            </td></tr>

            <!-- 16px spacer -->
            <tr><td style="height:12px;"></td></tr>

            <!-- Proposed vs Current strip -->
            <tr><td style="background:#fff;border:1px solid #EDEAE9;border-radius:12px;padding:20px 24px;">
              <table width="100%" cellpadding="0" cellspacing="0" border="0">
              <tr>
                <td width="50%" style="vertical-align:top;padding-right:12px;border-right:1px solid #E5E5E7;">
                  <p style="font-size:10px;font-weight:700;letter-spacing:0.5px;color:#9C9CA0;text-transform:uppercase;margin:0 0 6px;">Proposed / at risk</p>
                  <p style="font-size:13px;color:#1E1F21;margin:0;line-height:1.5;">{WebUtility.HtmlEncode(ctx.ProposedSummary)}</p>
                </td>
                <td width="50%" style="vertical-align:top;padding-left:12px;">
                  <p style="font-size:10px;font-weight:700;letter-spacing:0.5px;color:#9C9CA0;text-transform:uppercase;margin:0 0 6px;">Current position</p>
                  <p style="font-size:13px;color:#1E1F21;margin:0;line-height:1.5;">{WebUtility.HtmlEncode(ctx.CurrentPositionSummary)}</p>
                </td>
              </tr>
              </table>
            </td></tr>

            <tr><td style="height:12px;"></td></tr>
            """);

        // Q1–Q5 boxes
        var qs = new[]
        {
            ("Q1. What are we trying to accomplish?",            cp.Q1Answer),
            ("Q2. What is our current commercial position?",     cp.Q2Answer),
            ("Q3. What is helping, preventing, or changing?",    cp.Q3Answer),
            ("Q4. What matters most right now?",                 cp.Q4Answer),
            ("Q5. What should happen next?",                     cp.Q5Answer),
        };

        foreach (var (heading, answer) in qs)
        {
            sb.Append($"""
                <tr><td style="background:#fff;border:1px solid #EDEAE9;border-radius:12px;padding:20px 24px;margin-bottom:10px;">
                  <p style="font-size:11px;font-weight:700;letter-spacing:0.4px;color:#9C9CA0;text-transform:uppercase;margin:0 0 8px;">{WebUtility.HtmlEncode(heading)}</p>
                  <p style="font-size:13px;color:#1E1F21;line-height:1.65;margin:0;white-space:pre-line;">{WebUtility.HtmlEncode(answer)}</p>
                </td></tr>
                <tr><td style="height:10px;"></td></tr>
                """);
        }

        // Buttons
        sb.Append($"""
            <tr><td style="padding:8px 0;">
              <table width="100%" cellpadding="0" cellspacing="0" border="0"><tr>
                <td width="48%" style="padding-right:6px;">
                  <a href="{WebUtility.HtmlEncode(ctx.ViewEvidenceUrl)}"
                     style="display:block;background:#F0F0F2;color:#1E1F21;text-align:center;padding:11px 16px;border-radius:8px;font-size:13px;font-weight:600;text-decoration:none;border:1px solid #EDEAE9;">
                    View evidence
                  </a>
                </td>
                <td width="48%" style="padding-left:6px;">
                  <a href="{WebUtility.HtmlEncode(ctx.PostUpdateUrl)}"
                     style="display:block;background:#F06A6A;color:#fff;text-align:center;padding:11px 16px;border-radius:8px;font-size:13px;font-weight:600;text-decoration:none;">
                    {WebUtility.HtmlEncode(updateBtnLabel)}
                  </a>
                </td>
              </tr></table>
            </td></tr>

            <tr><td style="height:16px;"></td></tr>

            <!-- Flag line -->
            <tr><td style="text-align:center;padding:4px 0 24px;">
              <a href="{WebUtility.HtmlEncode(ctx.FlagUrl)}"
                 style="font-size:12px;color:#9C9CA0;text-decoration:underline;">{WebUtility.HtmlEncode(flagLine)}</a>
            </td></tr>

            </table>
            </td></tr>
            </table>
            </body>
            </html>
            """);

        return sb.ToString();
    }

    internal static string RenderPlainText(
        ReviewEmailContext ctx, string subject, string flagLine, string updateBtnLabel)
    {
        var cp       = ctx.Checkpoint;
        var prevDate = ctx.PreviousReviewDateUtc.HasValue
            ? ctx.PreviousReviewDateUtc.Value.ToString("d MMM yyyy")
            : "First tracked review";

        var sb = new StringBuilder();
        sb.AppendLine($"KOZMO — {subject.ToUpperInvariant()}");
        sb.AppendLine(new string('=', Math.Min(subject.Length + 9, 70)));
        sb.AppendLine();
        sb.AppendLine("VENDOR DETAILS");
        sb.AppendLine($"  Vendor:          {ctx.VendorName}");
        sb.AppendLine($"  Contract:        {ctx.ContractSummary}");
        sb.AppendLine($"  Meeting:         {ctx.MeetingTimeUtc:ddd d MMM yyyy, HH:mm} UTC");
        sb.AppendLine($"  Previous review: {prevDate}");
        sb.AppendLine($"  Renewal stage:   {ctx.RenewalStagePhrase}");
        sb.AppendLine();
        sb.AppendLine($"STATUS: {cp.Status} | MOVEMENT: {cp.Movement} | CONFIDENCE: {cp.Confidence}");
        sb.AppendLine();
        sb.AppendLine("PROPOSED / AT RISK");
        sb.AppendLine($"  {ctx.ProposedSummary}");
        sb.AppendLine();
        sb.AppendLine("CURRENT POSITION");
        sb.AppendLine($"  {ctx.CurrentPositionSummary}");
        sb.AppendLine();

        var qs = new[]
        {
            ("Q1. What are we trying to accomplish?",         cp.Q1Answer),
            ("Q2. What is our current commercial position?",  cp.Q2Answer),
            ("Q3. What is helping, preventing, or changing?", cp.Q3Answer),
            ("Q4. What matters most right now?",              cp.Q4Answer),
            ("Q5. What should happen next?",                  cp.Q5Answer),
        };

        foreach (var (heading, answer) in qs)
        {
            sb.AppendLine(heading.ToUpperInvariant());
            sb.AppendLine(answer);
            sb.AppendLine();
        }

        sb.AppendLine(new string('-', 50));
        sb.AppendLine($"View evidence:  {ctx.ViewEvidenceUrl}");
        sb.AppendLine($"{updateBtnLabel}: {ctx.PostUpdateUrl}");
        sb.AppendLine();
        sb.AppendLine(flagLine);
        sb.AppendLine(ctx.FlagUrl);

        return sb.ToString();
    }
}

// Allow the file-scope helpers to be called from the renderers
internal static class ReviewEmailRenderHelperProxy
{
    internal static string RenderHtml(ReviewEmailContext ctx, string title, string flagLine, string updateBtnLabel)
        => ReviewEmailRenderHelper.RenderHtml(ctx, title, flagLine, updateBtnLabel);

    internal static string RenderPlainText(ReviewEmailContext ctx, string subject, string flagLine, string updateBtnLabel)
        => ReviewEmailRenderHelper.RenderPlainText(ctx, subject, flagLine, updateBtnLabel);
}
