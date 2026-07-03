namespace Ii.CandidateExtraction;

/// <summary>
/// System prompt and user-prompt builder for the dimension-fact belief extraction LLM call.
/// Tunable: changing System causes all cassette keys to change (SHA-256 includes the system
/// prompt), so a cassette re-record pass is required after every prompt edit.
/// <para>
/// Commit-1 scope is exactly five facts — the ones <c>SaasQuestionBank</c> asks about that a
/// single document can plausibly answer: sla_uptime, csat, payment_terms, renewal_date,
/// annual_value. Keys match <c>catalogue/profiles/saas/claim_key_catalogue.saas.v1.json</c>
/// exactly — never invent a criterion name that isn't in that catalogue.
/// </para>
/// </summary>
public static class BeliefExtractionPrompt
{
    /// <summary>Max tokens for the extraction response. Five short facts plus reasoning.</summary>
    public const int MaxTokens = 800;

    /// <summary>Max characters of document text included in the user prompt.</summary>
    private const int MaxDocChars = 15_000;

    /// <summary>
    /// The only criterion keys this prompt asks about. <see cref="DocumentBeliefExtractor"/>
    /// drops any fact the model returns outside this set, even if it is otherwise well-formed.
    /// </summary>
    public static readonly IReadOnlySet<string> TargetCriteria = new HashSet<string>(
        StringComparer.OrdinalIgnoreCase)
    {
        "sla_uptime", "csat", "payment_terms", "renewal_date", "annual_value"
    };

    public const string System = """
        You are a strict commercial-intelligence fact extractor. Extract ONLY the five facts
        listed below, and ONLY when a value is EXPLICITLY stated in the document text. Do NOT
        infer, estimate, approximate, or use outside knowledge.

        FACTS (criterion key -> what to look for -> raw value encoding):
        - sla_uptime: an explicit committed or reported uptime/SLA percentage
          (e.g. "99.9% uptime"). value = the percentage number as given (e.g. 99.9),
          NOT divided by 100.
        - csat: an explicit customer satisfaction / quality score on a 1.0-5.0 rating scale ONLY
          (e.g. "4.6 out of 5.0", "CSAT rating: 4.2/5"). value = the score exactly as given
          (e.g. 4.6). Do NOT extract a CSAT figure given as a percentage or on a 0-100 scale
          (e.g. "CSAT: 92%") — that is a different scale this fact does not cover; omit it.
        - payment_terms: the invoice payment due period ONLY (e.g. "due within 30 days of
          invoice"). value = integer days (e.g. 30). Do NOT use insurance, cancellation, or
          termination notice periods for this fact.
        - renewal_date: an explicit CALENDAR date the agreement renews or is next due for
          renewal (e.g. "renews on September 1, 2026"). value = that date as a Unix timestamp
          (seconds since epoch, UTC midnight). An auto-renewal clause with no specific date is a
          RULE, not a date — omit renewal_date unless a concrete calendar date is stated.
        - annual_value: an explicit contract price or subscription fee paid by the customer.
          value = the dollar amount as a plain number (e.g. 250000 for "$250,000/year").
          Insurance requirements, liability caps, and indemnification ceilings are NOT fees.

        ABSTENTION IS MANDATORY: if a fact is not explicitly present in the document, DO NOT
        include it in the output — omit it entirely. Never invent, guess, or infer a value that
        is not stated. If NONE of the five facts appear anywhere in the document, return an
        empty "facts" array. An empty array is a correct, expected answer for many documents
        (e.g. marketing brochures, meeting notes) — it is not a failure.

        Every fact you DO include must carry the exact quoted span of text it was drawn from, so
        the claim can be checked against the source.

        Return JSON with this exact shape — no markdown fences, just the JSON object:
        {
          "facts": [
            {
              "criterion": "<sla_uptime|csat|payment_terms|renewal_date|annual_value>",
              "value": <number>,
              "evidence": "<exact quoted span from the document text>",
              "confidence": <float 0.0-1.0>
            }
          ],
          "confidence": <float 0.0-1.0>,
          "reasoning": "<one sentence>"
        }
        """;

    public static string User(string documentText)
    {
        var text = documentText.Length > MaxDocChars
            ? documentText[..MaxDocChars] + "\n[... truncated ...]"
            : documentText;
        return $"Document text:\n\n{text}";
    }
}
