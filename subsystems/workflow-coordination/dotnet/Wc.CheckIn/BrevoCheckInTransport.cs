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
/// Demo-day scope: sends every check-in to one configured recipient (a real vendor-contact
/// resolution is a separate, larger feature — this is the "one vendor, one gap" transport, not
/// the production routing story).
///
/// All config (SMTP login/key, sender identity, recipient) is supplied by the caller — the SMTP
/// key specifically comes from config/environment at the call site (Program.cs), never hardcoded,
/// never written to any file. Same seam InAppCheckInTransport already established; this is the
/// "real-email implementation swaps in here with no changes to the loop" its own doc comment
/// predicted.
/// </summary>
public sealed class BrevoCheckInTransport : ICheckInTransport
{
    private readonly string _smtpHost;
    private readonly int    _smtpPort;
    private readonly string _smtpLogin;
    private readonly string _smtpKey;
    private readonly string _senderEmail;
    private readonly string _senderName;
    private readonly string _recipientEmail;
    private readonly string _recipientName;

    public BrevoCheckInTransport(
        string smtpHost,
        int    smtpPort,
        string smtpLogin,
        string smtpKey,
        string senderEmail,
        string senderName,
        string recipientEmail,
        string recipientName)
    {
        _smtpHost       = smtpHost;
        _smtpPort       = smtpPort;
        _smtpLogin      = smtpLogin;
        _smtpKey        = smtpKey;
        _senderEmail    = senderEmail;
        _senderName     = senderName;
        _recipientEmail = recipientEmail;
        _recipientName  = recipientName;
    }

    public async Task SendAsync(CheckIn checkIn, CancellationToken ct = default)
    {
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(_senderName, _senderEmail));
        message.To.Add(new MailboxAddress(_recipientName, _recipientEmail));
        message.Subject = $"Kozmo check-in: {checkIn.Question}";

        var textBody =
            $"{checkIn.Question}\n\n" +
            $"Check-in ID: {checkIn.CheckInId}\n" +
            $"Vendor ID:   {checkIn.VendorId}\n" +
            $"Response shape: {checkIn.ResponseShape}\n\n" +
            "Reply to this email with your answer, or answer it directly in the Kozmo Pending queue.";

        var builder = new BodyBuilder
        {
            TextBody = textBody,
            HtmlBody =
                $"<p>{System.Net.WebUtility.HtmlEncode(checkIn.Question)}</p>" +
                $"<p><b>Check-in ID:</b> {checkIn.CheckInId}<br/>" +
                $"<b>Vendor ID:</b> {checkIn.VendorId}<br/>" +
                $"<b>Response shape:</b> {checkIn.ResponseShape}</p>" +
                "<p>Reply to this email with your answer, or answer it directly in the Kozmo Pending queue.</p>",
        };
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
}
