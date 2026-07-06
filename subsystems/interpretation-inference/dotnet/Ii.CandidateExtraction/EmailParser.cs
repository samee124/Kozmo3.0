using System.Text;
using MimeKit;
using MimeKit.Text;

namespace Ii.CandidateExtraction;

/// <summary>
/// Parses a real RFC 5322 / MIME .eml file into a <see cref="ParsedEmail"/> — sender, recipients,
/// date, subject, decoded body, thread/reference headers. E-signal Part 5 Step 2: parsing only, no
/// interpretation, no LLM call. MimeKit handles RFC 2047 encoded-word headers (e.g. encoded Subject
/// lines) and MIME transfer-encoding (base64/quoted-printable) transparently.
/// </summary>
public static class EmailParser
{
    public static ParsedEmail ParseFile(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        return Parse(stream, Path.GetFileName(filePath));
    }

    public static ParsedEmail Parse(Stream stream, string fileName)
    {
        var message = MimeMessage.Load(stream);

        var from = ToParty(message.From.Mailboxes.FirstOrDefault());
        var to   = message.To.Mailboxes.Select(ToParty).Where(p => p is not null).Select(p => p!).ToList();
        var cc   = message.Cc.Mailboxes.Select(ToParty).Where(p => p is not null).Select(p => p!).ToList();

        // MimeMessage.Date defaults to DateTimeOffset.MinValue when the Date header is absent —
        // surface that honestly as null rather than a fabricated timestamp.
        DateTimeOffset? date = message.Date == default ? null : message.Date;

        var references = message.References?.ToList() ?? [];

        return new ParsedEmail(
            FileName:   fileName,
            From:       from,
            To:         to,
            Cc:         cc,
            Date:       date,
            Subject:    message.Subject ?? "",
            Body:       ExtractBody(message),
            MessageId:  message.MessageId,
            InReplyTo:  message.InReplyTo,
            References: references);
    }

    private static EmailParty? ToParty(MailboxAddress? mailbox) =>
        mailbox is null ? null : new EmailParty(mailbox.Name, mailbox.Address);

    /// <summary>
    /// E-signal Part 5 Step 6 — builds the text handed to <see cref="DocumentCandidateExtractor"/>
    /// for identity resolution (spec §2.4 Decision 3: email reuses the existing
    /// DocumentCandidateExtractor/ClusteringStage path, not a new mechanism). Prepending the
    /// From/To/Cc header lines (not just the body) lets the SAME extraction prompt — which already
    /// asks for "domain if visible in text" — pick up sender/recipient domains from the real
    /// mailbox addresses, deterministically parsed by <see cref="EmailParser"/>, without any new
    /// domain-parsing code. Shared by <c>Kyv.EmailCandidateRecorder</c> and
    /// <c>Kyv.ProgramRunner</c> so the recorded cassette key matches the live pipeline's call
    /// byte-for-byte.
    /// </summary>
    public static string BuildIdentityText(ParsedEmail email)
    {
        var sb = new StringBuilder();

        if (email.From is not null)
            sb.Append("From: ").Append(email.From.DisplayName).Append(" <").Append(email.From.Address).Append(">\n");
        if (email.To.Count > 0)
            sb.Append("To: ").Append(string.Join(", ", email.To.Select(t => $"{t.DisplayName} <{t.Address}>"))).Append('\n');
        if (email.Cc.Count > 0)
            sb.Append("Cc: ").Append(string.Join(", ", email.Cc.Select(c => $"{c.DisplayName} <{c.Address}>"))).Append('\n');
        sb.Append("Subject: ").Append(email.Subject).Append("\n\n");
        sb.Append(email.Body);

        return sb.ToString();
    }

    // Prefer the plain-text body; fall back to stripping tags from the HTML body (deterministic,
    // no LLM — MimeKit ships no direct HtmlToText converter, so tags are stripped by walking the
    // tokenizer and keeping only Data tokens) when no text/plain part exists. Neither part present
    // -> empty body, honest gap.
    private static string ExtractBody(MimeMessage message)
    {
        var text = message.TextBody;
        if (!string.IsNullOrWhiteSpace(text)) return text.Trim();

        var html = message.HtmlBody;
        if (string.IsNullOrWhiteSpace(html)) return "";

        return StripHtml(html).Trim();
    }

    private static string StripHtml(string html)
    {
        using var reader = new StringReader(html);
        var tokenizer = new HtmlTokenizer(reader);
        var sb = new StringBuilder();

        while (tokenizer.ReadNextToken(out var token))
        {
            if (token.Kind == HtmlTokenKind.Data)
                sb.Append(((HtmlDataToken)token).Data);
        }

        return sb.ToString();
    }
}
