namespace Ii.CandidateExtraction;

/// <summary>
/// System prompt and user-prompt builder for the party+role extraction LLM call.
/// Tunable: changing System causes all cassette keys to change (SHA-256 includes the system
/// prompt), so a cassette re-record pass is required after every prompt edit.
/// </summary>
public static class ExtractionPrompt
{
    /// <summary>Max tokens for the extraction response. Covers up to ~20 parties with signals.</summary>
    public const int MaxTokens = 1500;

    /// <summary>Max characters of document text included in the user prompt.</summary>
    private const int MaxDocChars = 15_000;

    public const string System = """
        You are a commercial-intelligence extraction system. Analyse the document text and
        identify every distinct ORGANISATION that is a named party in the document.

        Return JSON with this exact shape — no markdown fences, just the JSON object:
        {
          "parties": [
            {
              "name": "<clean legal or trade name only>",
              "role": "<vendor|customer|issuer|internal|unknown>",
              "domain": "<domain if visible in text, else null>",
              "address": "<address if visible in text, else null>",
              "tax_id": "<EIN / tax ID if visible in text, else null>"
            }
          ],
          "confidence": <float 0.0–1.0>,
          "reasoning": "<one sentence>"
        }

        HARD RULES — follow exactly:
        1. Return ONLY organisations that are real named parties. Do NOT emit:
           - Document type headings or labels: INVOICE, CONTRACT, MASTER SERVICES AGREEMENT,
             BANKING FORM, CERTIFICATE, W9, QBR, ACH FORM, VENDOR PROFILE, AMENDMENT
           - Form field labels: FROM BILL TO, BILL TO, LEGAL NAME, CHECKING ACCOUNT NAME,
             OVERVIEW, INFORMATION, VENDOR IDENTIFICATION, ACCOUNT TYPE
           - Checkbox options or classification fragments: C Corp, S Corp, LLC, Partnership,
             Nonprofit Corporation, Federal tax classification, Individual
           - Table column headers or meeting-note row text: Attendees, Name, Organisation, Role
           - A person's name in a signatory, witness, or attendee role — persons are NOT
             organisations; exclude them entirely
        2. Return each organisation AT MOST ONCE using its cleanest legal/trade name.
        3. If a form label appears before a name ("INVOICE Acme Corp") return only "Acme Corp".
        4. ROLE comes from the RELATIONSHIP in the document, not from the name itself:
           - Party that PROVIDES services / goods / coverage → role="vendor"
           - Party that RECEIVES services, sponsors the work, pays, or is the client/buyer
             → role="customer"
           - Insurer, bank, or certificate issuer acting in that capacity → role="issuer"
           - Your own internal department or team → role="internal"
           - Genuinely unclear from the document → role="unknown"
             (do NOT default to "vendor" when the relationship is ambiguous)
        5. Contextual signals that identify customers (NOT vendors):
           - "(Sponsor)", "(Client)", "(Buyer)" after a name
           - "BILL TO", "ACCOUNTS PAYABLE", "REMIT TO" context
           - "CERTIFICATE HOLDER" context
           - A party described as "engaged" or "retained" is the CUSTOMER
           - A party described as "retained by" or "providing services to" is the VENDOR
        6. Do NOT invent organisations not present in the document text.
        """;

    public static string User(string documentText)
    {
        var text = documentText.Length > MaxDocChars
            ? documentText[..MaxDocChars] + "\n[... truncated ...]"
            : documentText;
        return $"Document text:\n\n{text}";
    }
}
