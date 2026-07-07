using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using Wc.Contracts;

namespace Wc.CheckIn;

using CheckIn = global::Wc.Contracts.CheckIn;

/// <summary>
/// Real email transport for check-ins via Brevo's SMTP relay (smtp-relay.brevo.com:587, STARTTLS).
/// Uses MailKit rather than System.Net.Mail.SmtpClient — the latter failed live against this relay
/// with "5.7.0 Please authenticate first" even with credentials/UseDefaultCredentials set correctly,
/// a known class of STARTTLS/AUTH reliability issue with the BCL client that MailKit doesn't share.
///
/// Accepts a batch of check-ins and renders one branded digest email per call using an
/// email-client-safe table layout (VML fallbacks for Outlook, inline CSS throughout).
/// YES_NO questions get inline Yes / No / Unsure answer links; STATUS_SELECT and TYPED_VALUE
/// questions get an "Answer in Kozmo →" deep-link button. A "View full queue →" CTA at the
/// bottom links to /pending.
///
/// All config (SMTP login/key, sender identity, recipient) is supplied by the caller — the SMTP
/// key specifically comes from config/environment at the call site (Program.cs), never hardcoded.
/// Recipient is resolved at send time from the logged-in Google account via recipientResolver.
/// Base UI URL defaults to http://localhost:3000; override with KOZMO_UI_BASE_URL env var.
/// </summary>
public sealed class BrevoCheckInTransport : ICheckInTransport
{
    private readonly string                                  _smtpHost;
    private readonly int                                     _smtpPort;
    private readonly string                                  _smtpLogin;
    private readonly string                                  _smtpKey;
    private readonly string                                  _senderEmail;
    private readonly string                                  _senderName;
    private readonly Func<CancellationToken, Task<string?>>  _recipientResolver;
    private readonly string                                  _recipientName;
    private readonly CheckInTokenOptions?                    _tokenOptions;

    public BrevoCheckInTransport(
        string                                           smtpHost,
        int                                              smtpPort,
        string                                           smtpLogin,
        string                                           smtpKey,
        string                                           senderEmail,
        string                                           senderName,
        Func<CancellationToken, Task<string?>>           recipientResolver,
        string                                           recipientName,
        CheckInTokenOptions?                             tokenOptions = null)
    {
        _smtpHost          = smtpHost;
        _smtpPort          = smtpPort;
        _smtpLogin         = smtpLogin;
        _smtpKey           = smtpKey;
        _senderEmail       = senderEmail;
        _senderName        = senderName;
        _recipientResolver = recipientResolver;
        _recipientName     = recipientName;
        _tokenOptions      = tokenOptions;
    }

    public async Task SendAsync(IReadOnlyList<CheckIn> checkIns, CancellationToken ct = default)
    {
        if (checkIns.Count == 0) return;

        var recipientEmail = await _recipientResolver(ct);
        if (string.IsNullOrWhiteSpace(recipientEmail))
            return; // no logged-in user email — skip silently

        var count      = checkIns.Count;
        var vendorId   = checkIns[0].VendorId;
        var runId      = checkIns[0].ProgramRunId;
        var vendorName = $"Vendor {vendorId.ToString("N")[..8].ToUpper()}";
        var uiBaseUrl  = _tokenOptions?.UiBaseUrl
                         ?? Environment.GetEnvironmentVariable("KOZMO_UI_BASE_URL")
                         ?? "http://localhost:3000";
        var apiBaseUrl = _tokenOptions?.ApiBaseUrl
                         ?? Environment.GetEnvironmentVariable("KOZMO_API_BASE_URL")
                         ?? "http://localhost:5000";
        var pendingUrl = $"{uiBaseUrl}/pending";
        var unsubUrl   = $"{uiBaseUrl}/unsubscribe";
        var tokenNow   = DateTimeOffset.UtcNow;

        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(_senderName, _senderEmail));
        message.To.Add(new MailboxAddress(_recipientName, recipientEmail));
        message.Subject = $"Kozmo — {count} open question{(count == 1 ? "" : "s")} for {vendorName}";

        // Build per-question rows for both text and HTML bodies.
        var textRows = new System.Text.StringBuilder();
        var htmlRows = new System.Text.StringBuilder();

        for (var i = 0; i < checkIns.Count; i++)
        {
            var ci     = checkIns[i];
            var isLast = i == checkIns.Count - 1;
            htmlRows.Append(BuildQuestionRowHtml(i + 1, ci, uiBaseUrl, apiBaseUrl, _tokenOptions, tokenNow, isLast));

            var pendingLink = $"{uiBaseUrl}/pending?highlight={ci.CheckInId}";
            textRows.AppendLine($"{i + 1}. {ci.Question}");
            if (ci.ResponseShape == ResponseShape.YES_NO && _tokenOptions is not null)
            {
                var yesToken = CheckInLinkToken.Generate(ci.CheckInId, "YES", _tokenOptions.Secret, _tokenOptions.TtlDays, tokenNow);
                var noToken  = CheckInLinkToken.Generate(ci.CheckInId, "NO",  _tokenOptions.Secret, _tokenOptions.TtlDays, tokenNow);
                textRows.AppendLine($"   Yes    → {apiBaseUrl}/check-ins/{ci.CheckInId}/confirm?token={yesToken}");
                textRows.AppendLine($"   No     → {apiBaseUrl}/check-ins/{ci.CheckInId}/confirm?token={noToken}");
                textRows.AppendLine($"   Unsure → {pendingLink}");
            }
            else if (ci.ResponseShape == ResponseShape.YES_NO)
            {
                textRows.AppendLine($"   Yes    → {pendingLink}&answer=YES");
                textRows.AppendLine($"   No     → {pendingLink}&answer=NO");
                textRows.AppendLine($"   Unsure → {pendingLink}&answer=UNSURE");
            }
            else
            {
                textRows.AppendLine($"   Answer → {pendingLink}");
            }
            textRows.AppendLine($"   REF {ci.CheckInId}");
            textRows.AppendLine();
        }

        var textBody =
            $"Kozmo — {count} open question{(count == 1 ? "" : "s")} for {vendorName}\n\n" +
            textRows.ToString() +
            $"View full queue: {pendingUrl}";

        var htmlBody = BuildEmailHtml(
            count, vendorName, vendorId, runId,
            htmlRows.ToString(), pendingUrl, unsubUrl);

        var builder = new BodyBuilder { TextBody = textBody, HtmlBody = htmlBody };
        message.Body = builder.ToMessageBody();

        // AUTH: the Brevo SMTP LOGIN (a fixed account identifier, e.g. "9f924d001@smtp-brevo.com"),
        // never the sender email — the two are deliberately separate. The FROM header above is the
        // verified sender identity; the login below is only for authenticating to the relay.
        using var client = new SmtpClient();
        await client.ConnectAsync(_smtpHost, _smtpPort, SecureSocketOptions.StartTls, ct);
        await client.AuthenticateAsync(_smtpLogin, _smtpKey, ct);
        await client.SendAsync(message, ct);
        await client.DisconnectAsync(true, ct);
    }

    // ── HTML builders ─────────────────────────────────────────────────────────

    /// <summary>Renders one question card row matching the Kozmo email template design.</summary>
    private static string BuildQuestionRowHtml(
        int number, CheckIn ci,
        string uiBaseUrl, string apiBaseUrl,
        CheckInTokenOptions? tokenOptions, DateTimeOffset tokenNow,
        bool isLast)
    {
        var qEsc       = System.Net.WebUtility.HtmlEncode(ci.Question);
        var pendingLink = $"{uiBaseUrl}/pending?highlight={ci.CheckInId}";
        var padding    = isLast ? "0 24px 20px 24px" : "0 24px 10px 24px";

        string answerHtml;
        string qMarginBottom;
        if (ci.ResponseShape == ResponseShape.YES_NO)
        {
            qMarginBottom = "10px";

            string yesUrl, noUrl, unsureUrl;
            if (tokenOptions is not null)
            {
                var yesToken = CheckInLinkToken.Generate(ci.CheckInId, "YES", tokenOptions.Secret, tokenOptions.TtlDays, tokenNow);
                var noToken  = CheckInLinkToken.Generate(ci.CheckInId, "NO",  tokenOptions.Secret, tokenOptions.TtlDays, tokenNow);
                yesUrl    = $"{apiBaseUrl}/check-ins/{ci.CheckInId}/confirm?token={Uri.EscapeDataString(yesToken)}";
                noUrl     = $"{apiBaseUrl}/check-ins/{ci.CheckInId}/confirm?token={Uri.EscapeDataString(noToken)}";
                unsureUrl = pendingLink; // UNKNOWN cannot be recorded via YES_NO validator — go to pending queue
            }
            else
            {
                yesUrl    = $"{pendingLink}&answer=YES";
                noUrl     = $"{pendingLink}&answer=NO";
                unsureUrl = $"{pendingLink}&answer=UNSURE";
            }

            answerHtml =
                $"<p style=\"margin:0;font-family:Arial,Helvetica,sans-serif;font-size:13px;\">" +
                $"<a href=\"{yesUrl}\" style=\"color:#2F6B4F;font-weight:600;text-decoration:none;\">&#9675; Yes</a>" +
                $"<span style=\"color:#D1D1D1;\">&nbsp;&nbsp;&nbsp;</span>" +
                $"<a href=\"{noUrl}\" style=\"color:#B04A3A;font-weight:600;text-decoration:none;\">&#9675; No</a>" +
                $"<span style=\"color:#D1D1D1;\">&nbsp;&nbsp;&nbsp;</span>" +
                $"<a href=\"{unsureUrl}\" style=\"color:#9AA0A6;font-weight:600;text-decoration:none;\">&#9675; Unsure</a>" +
                $"</p>";
        }
        else
        {
            qMarginBottom = "12px";
            answerHtml =
                $"<a href=\"{pendingLink}\" style=\"display:inline-block;background:#171B24;color:#FFFFFF;" +
                $"text-decoration:none;padding:8px 16px;border-radius:20px;" +
                $"font-family:Arial,Helvetica,sans-serif;font-size:12px;font-weight:600;\">" +
                $"Answer in Kozmo &#8594;</a>";
        }

        return
            $"<tr>\n" +
            $"  <td class=\"px\" style=\"padding:{padding};\">\n" +
            $"    <table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" style=\"background:#FAFAFA;border:1px solid #ECECEC;border-radius:14px;\">\n" +
            $"      <tr><td style=\"padding:16px 18px;\">\n" +
            $"        <table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\">\n" +
            $"          <tr>\n" +
            $"            <td style=\"width:26px;vertical-align:top;\">\n" +
            $"              <table role=\"presentation\" cellpadding=\"0\" cellspacing=\"0\" style=\"width:22px;\">" +
            $"<tr><td align=\"center\" valign=\"middle\" style=\"width:22px;height:22px;background:#171B24;border-radius:50%;\">" +
            $"<span style=\"font-family:Arial,Helvetica,sans-serif;font-size:10px;font-weight:bold;color:#FFFFFF;\">{number}</span>" +
            $"</td></tr></table>\n" +
            $"            </td>\n" +
            $"            <td style=\"vertical-align:top;padding-left:10px;\">\n" +
            $"              <p style=\"margin:0 0 {qMarginBottom} 0;font-family:Arial,Helvetica,sans-serif;font-size:14px;line-height:1.45;color:#171B24;font-weight:600;\">{qEsc}</p>\n" +
            $"              {answerHtml}\n" +
            $"              <p style=\"margin:10px 0 0 0;font-family:'SFMono-Regular',Consolas,'Liberation Mono',Menlo,monospace;font-size:9px;color:#BEBEBE;\">REF {ci.CheckInId}</p>\n" +
            $"            </td>\n" +
            $"          </tr>\n" +
            $"        </table>\n" +
            $"      </td></tr>\n" +
            $"    </table>\n" +
            $"  </td>\n" +
            $"</tr>\n";
    }

    /// <summary>
    /// Assembles the full email-safe HTML document using the Kozmo branded template.
    /// CSS is kept in a separate non-interpolated literal to avoid escaping curly braces.
    /// </summary>
    private static string BuildEmailHtml(
        int count, string vendorName, Guid vendorId, Guid runId,
        string questionRows, string pendingUrl, string unsubUrl)
    {
        // CSS extracted to avoid {{ }} escaping inside the interpolated template below.
        const string css =
            "body,table,td{margin:0;padding:0;}" +
            "img{border:0;display:block;}" +
            "table{border-collapse:collapse;}" +
            "@media only screen and (max-width:600px){" +
            ".email-container{width:100% !important;}" +
            ".px{padding-left:16px !important;padding-right:16px !important;}" +
            "}";

        var vendorNameEsc = System.Net.WebUtility.HtmlEncode(vendorName);
        var plural        = count == 1 ? "" : "s";

        return
            "<!DOCTYPE html>" +
            "<html lang=\"en\" xmlns=\"http://www.w3.org/1999/xhtml\" xmlns:v=\"urn:schemas-microsoft-com:vml\" xmlns:o=\"urn:schemas-microsoft-com:office:office\">" +
            "<head>" +
            "<meta charset=\"utf-8\">" +
            "<meta name=\"viewport\" content=\"width=device-width, initial-scale=1.0\">" +
            "<meta http-equiv=\"X-UA-Compatible\" content=\"IE=edge\">" +
            "<title>Kozmo</title>" +
            "<!--[if mso]><noscript><xml><o:OfficeDocumentSettings><o:PixelsPerInch>96</o:PixelsPerInch></o:OfficeDocumentSettings></xml></noscript><![endif]-->" +
            $"<style>{css}</style>" +
            "</head>" +
            "<body style=\"margin:0;padding:0;background:#F2F3F5;\">" +

            // Preheader
            $"<div style=\"display:none;max-height:0;overflow:hidden;opacity:0;\">Kozmo &#8212; {count} open question{plural} for {vendorNameEsc}. Tap to answer.</div>" +

            // Outer wrapper
            "<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" style=\"background:#F2F3F5;\">" +
            "<tr><td align=\"center\" style=\"padding:28px 16px;\">" +
            "<table role=\"presentation\" class=\"email-container\" width=\"560\" cellpadding=\"0\" cellspacing=\"0\" style=\"width:560px;max-width:560px;background:#FFFFFF;border-radius:20px;overflow:hidden;\">" +

            // Header
            "<tr>" +
            "  <td style=\"background:#171B24;padding:20px 24px;\">" +
            "    <table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\">" +
            "      <tr>" +
            "        <td style=\"width:30px;vertical-align:middle;\">" +
            "          <table role=\"presentation\" cellpadding=\"0\" cellspacing=\"0\" style=\"width:26px;\">" +
            "            <tr><td align=\"center\" valign=\"middle\" style=\"width:26px;height:26px;background:#A9762F;border-radius:50%;\">" +
            "              <span style=\"font-family:Arial,Helvetica,sans-serif;font-size:12px;font-weight:bold;color:#171B24;\">K</span>" +
            "            </td></tr>" +
            "          </table>" +
            "        </td>" +
            "        <td style=\"vertical-align:middle;padding-left:10px;\">" +
            "          <span style=\"font-family:Arial,Helvetica,sans-serif;font-size:15px;font-weight:bold;letter-spacing:0.5px;color:#FFFFFF;\">Kozmo</span>" +
            "        </td>" +
            "        <td align=\"right\" style=\"vertical-align:middle;\">" +
            $"          <span style=\"display:inline-block;background:#232938;border-radius:14px;padding:5px 12px;font-family:Arial,Helvetica,sans-serif;font-size:11px;color:#D9D6CC;\">{vendorNameEsc}</span>" +
            "        </td>" +
            "      </tr>" +
            "    </table>" +
            "  </td>" +
            "</tr>" +

            // Headline
            "<tr>" +
            "  <td class=\"px\" style=\"padding:22px 24px 4px 24px;\">" +
            $"    <h1 style=\"margin:0;font-family:Arial,Helvetica,sans-serif;font-size:19px;line-height:1.4;color:#171B24;font-weight:bold;\">{count} open question{plural} for {vendorNameEsc}</h1>" +
            "  </td>" +
            "</tr>" +
            "<tr>" +
            "  <td class=\"px\" style=\"padding:0 24px 18px 24px;\">" +
            "    <p style=\"margin:0;font-family:Arial,Helvetica,sans-serif;font-size:13px;line-height:1.5;color:#6B7280;\">Tap an answer below &#8212; each one saves individually.</p>" +
            "  </td>" +
            "</tr>" +

            // Question rows (injected)
            questionRows +

            // Primary CTA
            "<tr>" +
            "  <td class=\"px\" align=\"center\" style=\"padding:0 24px 26px 24px;\">" +
            "    <!--[if mso]>" +
            $"    <v:roundrect xmlns:v=\"urn:schemas-microsoft-com:vml\" href=\"{pendingUrl}\" style=\"height:44px;v-text-anchor:middle;width:200px;\" arcsize=\"50%\" fillcolor=\"#F2F3F5\" strokecolor=\"#D8D8D8\" strokeweight=\"1px\">" +
            "    <w:anchorlock/>" +
            "    <center style=\"color:#171B24;font-family:Arial,sans-serif;font-size:13px;font-weight:bold;\">View full queue &#8594;</center>" +
            "    </v:roundrect>" +
            "    <![endif]-->" +
            "    <!--[if !mso]><!-->" +
            $"    <a href=\"{pendingUrl}\" style=\"display:inline-block;background:#F2F3F5;border:1px solid #D8D8D8;color:#171B24;text-decoration:none;padding:12px 26px;border-radius:22px;font-family:Arial,Helvetica,sans-serif;font-size:13px;font-weight:600;\">View full queue &#8594;</a>" +
            "    <!--<![endif]-->" +
            "  </td>" +
            "</tr>" +

            // Footer
            "<tr>" +
            "  <td style=\"background:#FAFAFA;padding:16px 24px;border-top:1px solid #ECECEC;\">" +
            $"    <p style=\"margin:0;font-family:'SFMono-Regular',Consolas,'Liberation Mono',Menlo,monospace;font-size:9px;color:#B0B0B0;\">VENDOR {vendorId} &#183; RUN {runId}</p>" +
            "    <p style=\"margin:8px 0 0 0;font-family:Arial,Helvetica,sans-serif;font-size:11px;line-height:1.6;color:#9AA0A6;\">" +
            "      Answers save to the record automatically." +
            $"      &nbsp;&#183;&nbsp;<a href=\"{unsubUrl}\" style=\"color:#9AA0A6;text-decoration:underline;\">Unsubscribe</a>" +
            "    </p>" +
            "  </td>" +
            "</tr>" +

            "</table>" +
            "</td></tr>" +
            "</table>" +
            "</body>" +
            "</html>";
    }
}
