namespace Ii.CandidateExtraction;

/// <summary>
/// One parsed party (a mailbox address with an optional display name). <see cref="Domain"/> is
/// derived deterministically from the address — no LLM, no network — ready to feed
/// Ig.Contracts.CandidateSignals.Domain once email is wired into identity resolution
/// (E-signal Part 5 Step 3+); this step only parses, it does not construct that type yet.
/// </summary>
public sealed record EmailParty(string? DisplayName, string Address)
{
    public string? Domain
    {
        get
        {
            var at = Address.LastIndexOf('@');
            return at >= 0 && at < Address.Length - 1 ? Address[(at + 1)..].ToLowerInvariant() : null;
        }
    }
}

/// <summary>
/// One parsed .eml message — sender, recipients, date, subject, decoded body, and thread/reference
/// headers. Pure structured data: no interpretation, no belief/signal production (E-signal Part 5
/// Step 2). <see cref="Body"/> is the plain-text content (RFC 2047 header decoding and MIME
/// transfer-encoding are handled by MimeKit; an HTML-only body is converted to text).
/// </summary>
public sealed record ParsedEmail(
    string                FileName,
    EmailParty?           From,
    IReadOnlyList<EmailParty> To,
    IReadOnlyList<EmailParty> Cc,
    DateTimeOffset?       Date,
    string                Subject,
    string                Body,
    string?               MessageId,
    string?               InReplyTo,
    IReadOnlyList<string> References
);
