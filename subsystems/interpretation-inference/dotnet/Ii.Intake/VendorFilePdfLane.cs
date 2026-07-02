using System.Text;
using System.Text.Json;
using Kozmo.Contracts;
using Kozmo.Contracts.Config;
using Kozmo.Llm;
using Km.Store;

namespace Ii.Intake;

/// <summary>
/// PDF intake lane (§6). Isolated from the rules and signals lanes.
///
/// Pipeline:
///   1. Caller provides per-page text (page number → text).
///   2. One LLM call via IKozmoLlm returns structured claims (§4 structural claim_keys)
///      and the supporting quoted span for each.
///   3. For each claim the locator is built as "page:P §clause" from the LLM response.
///      Beliefs are PRIMARY (from signed contract per §3) and observed_at = the document's
///      effective date supplied by the caller — never the clock.
///
/// Determinism: rehearsal uses CachingLlmClient in RECORD mode; demo/tests replay from
/// the committed cassette. A cache miss in replay throws LlmCacheMissException.
/// </summary>
public sealed class VendorFilePdfLane
{
    private readonly IKozmoLlm             _llm;
    private readonly VendorFileWriteService _writeService;
    private readonly SaasProfile           _profile;

    // Fixed prompts — the cache key is a stable SHA-256 of (model|temp|system|user|maxTokens),
    // so these strings must never change without re-recording the cassette.
    private const string SystemPrompt =
        "You are a strict contract data extractor. Extract ONLY claims whose values are " +
        "EXPLICITLY present in the document. Do NOT infer, approximate, or substitute.\n\n" +
        "=== RULE 1 — BOOLEAN claims (auto_renewal) ===\n" +
        "The span MUST express the fact in ordinary contract language. " +
        "It will NOT contain \"1\" or \"true\" — that is expected and correct.\n" +
        "If the agreement text says it renews automatically, you MUST emit auto_renewal=1.\n" +
        "REQUIRED EXAMPLE: span = \"automatically renew for successive twelve (12) month periods\" " +
        "-> emit {\"claim_key\":\"auto_renewal\",\"raw_value\":1,\"quoted_span\":\"<that text>\",...}.\n\n" +
        "=== RULE 2 — NUMERIC claims (notice_period, payment_terms, annual_value, liability_cap) ===\n" +
        "The span must contain a CONCRETE number in digits or words.\n" +
        "VALID: \"one hundred eighty (180) days\" for notice_period=180.\n" +
        "VALID: \"within thirty (30) days\" for payment_terms=30.\n" +
        "FORMULA TEST: Before emitting any numeric claim, ask yourself: " +
        "\"Can I write a concrete integer or decimal here WITHOUT using 0 as a placeholder?\" " +
        "If the document gives a formula, relative amount, or conditional expression " +
        "(e.g. \"not to exceed fees paid during the six (6) month period\", " +
        "\"equal to two months of fees\"), the answer is NO — OMIT the claim entirely. " +
        "The value 0 means zero dollars/days; it does NOT mean unknown or formula-based. " +
        "NEVER emit a numeric claim with raw_value=0 unless the document literally states zero.\n\n" +
        "PAYMENT_TERMS SCOPE: payment_terms is EXCLUSIVELY the invoice payment due period " +
        "(e.g. \"invoices are due within 30 days of invoice date\"). " +
        "NEVER extract payment_terms from: insurance cancellation notice periods, contract " +
        "termination notice periods, cure periods, or any clause that is not about when a " +
        "customer payment invoice falls due. If a clause says \"30 days\" in an insurance, " +
        "cancellation, or notice context, ignore it for payment_terms.\n\n" +
        "ONE CLAIM PER KEY: Emit at most ONE object per claim_key in the output array. " +
        "If multiple passages contain a candidate value for the same key, choose the passage " +
        "whose context is directly about that claim (e.g. for payment_terms, use the " +
        "invoicing/payment section, not an insurance or notice clause) and discard the rest.\n\n" +
        "=== RULE 3 — DATE claims (renewal_date) ===\n" +
        "The span must contain an explicit calendar date (e.g. \"September 1, 2026\").\n" +
        "An auto-renewal clause stating the agreement renews for successive periods is a RULE, " +
        "not a date. OMIT renewal_date unless a specific calendar date is present.\n\n" +
        "=== RULE 4 — annual_value exclusions ===\n" +
        "Only emit annual_value for an explicit contract price or subscription fee paid by the " +
        "customer. Insurance requirements, liability caps, and indemnification ceilings are NOT " +
        "fees — OMIT annual_value when no contract price is stated.\n\n" +
        "Return JSON: {\"claims\":[{\"claim_key\":\"...\",\"raw_value\":0,\"confidence\":0.0," +
        "\"quoted_span\":\"...\",\"page\":1,\"clause\":\"...\"}]}\n\n" +
        "Valid claim_keys and raw_value encoding:\n" +
        "- auto_renewal: 1 (true) or 0 (false) — boolean; span expresses the fact\n" +
        "- notice_period: integer days — concrete number required\n" +
        "- payment_terms: integer days — concrete number required\n" +
        "- renewal_date: Unix timestamp — only if explicit calendar date present\n" +
        "- annual_value: numeric — only explicit contract fee; never insurance/liability\n" +
        "- liability_cap: numeric — only if a concrete dollar amount is stated; omit if formula\n\n" +
        "Omit any claim whose value is absent, relative, or a formula. " +
        "Include clause reference (e.g. \"§7.2\") or empty string.";

    private const int MaxTokens = 1000;

    private readonly PdfTextExtractor _pdfExtractor;

    public VendorFilePdfLane(
        IKozmoLlm              llm,
        VendorFileWriteService  writeService,
        SaasProfile             profile,
        PdfTextExtractor?       pdfExtractor = null)
    {
        _llm          = llm;
        _writeService = writeService;
        _profile      = profile;
        _pdfExtractor = pdfExtractor ?? new PdfTextExtractor();
    }

    /// <summary>
    /// Full live path: extract page texts from raw PDF bytes, then call the LLM once.
    /// When the backing IKozmoLlm is a CachingLlmClient in record mode the cassette is written
    /// on first call; subsequent calls with replay mode hit the cassette (no network).
    /// </summary>
    public Task<IReadOnlyList<Belief>> ExtractFromBytesAndWriteAsync(
        Guid           vendorId,
        Evidence       evidence,
        byte[]         pdfBytes,
        DateTimeOffset effectiveDate,
        CancellationToken ct = default)
    {
        var pageTexts = _pdfExtractor.ExtractPageTexts(pdfBytes);
        return ExtractAndWriteAsync(vendorId, evidence, pageTexts, effectiveDate, ct);
    }

    /// <summary>
    /// Extract structural claims from the contract via one LLM call and write PRIMARY beliefs.
    /// pageTexts: page number (1-based) → extracted text from that page.
    /// effectiveDate: the document's effective date, used as observed_at for every belief.
    /// </summary>
    public async Task<IReadOnlyList<Belief>> ExtractAndWriteAsync(
        Guid                             vendorId,
        Evidence                         evidence,
        IReadOnlyDictionary<int, string> pageTexts,
        DateTimeOffset                   effectiveDate,
        CancellationToken                ct = default)
    {
        var userPrompt = BuildUserPrompt(pageTexts);
        var llmResult  = await _llm.CompleteJsonAsync(SystemPrompt, userPrompt, MaxTokens, ct);

        if (llmResult.Answer is not JsonElement doc) return [];
        if (!doc.TryGetProperty("claims", out var claimsEl)) return [];

        var results = new List<Belief>();

        foreach (var item in claimsEl.EnumerateArray())
        {
            var claimKey = item.GetProperty("claim_key").GetString() ?? "";
            if (!_profile.ClaimKeyCatalogue.TryGetValue(claimKey, out var ckDef)) continue;

            var rawValue   = item.GetProperty("raw_value").GetDouble();
            var confidence = item.TryGetProperty("confidence", out var cf) ? cf.GetDouble() : 0.9;
            var page       = item.TryGetProperty("page",   out var pg) ? pg.GetInt32()  : 1;
            var clause     = item.TryGetProperty("clause", out var cl) ? cl.GetString() ?? "" : "";

            // Provenance locator: "page:N §clause" (§6 format); omit clause if absent
            var locator = string.IsNullOrEmpty(clause) ? $"page:{page}" : $"page:{page} {clause}";

            // Structural claims have empty dimension string in the catalogue; use Financial
            // placeholder so the Belief record has a valid enum value (confidence=0 means
            // it never feeds the rubric regardless).
            if (!Enum.TryParse<Dimension>(ckDef.Dimension, ignoreCase: true, out var dimension))
                dimension = Dimension.Financial;

            var belief = await _writeService.WriteBeliefAsync(
                vendorId:            vendorId,
                claimKey:            claimKey,
                dimension:           dimension,
                criterion:           claimKey,
                rawValue:            rawValue,
                tier:                evidence.SourceTier,
                extractorConfidence: confidence,
                observedAt:          effectiveDate,
                provenance:          new BeliefProvenance(evidence.EvidenceId, locator),
                ingestedAt:          evidence.IngestedAt,
                ct:                  ct);

            results.Add(belief);
        }

        return results;
    }

    // User prompt: pages in order, each labelled [Page N].
    internal static string BuildUserPrompt(IReadOnlyDictionary<int, string> pageTexts)
    {
        var sb = new StringBuilder("Contract pages:");
        foreach (var (page, text) in pageTexts.OrderBy(kv => kv.Key))
            sb.Append($"\n\n[Page {page}]\n{text}");
        return sb.ToString();
    }
}
