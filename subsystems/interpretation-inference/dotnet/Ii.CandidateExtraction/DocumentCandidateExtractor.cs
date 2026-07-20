using System.Text.Json;
using Ig.Contracts;
using Kozmo.Contracts;
using Kozmo.Llm;

namespace Ii.CandidateExtraction;

/// <summary>
/// LLM-based party + role extractor (Phase 2, Commit 2).
///
/// One LLM read per document returns the distinct organisations that are parties,
/// each with a clean name and role. The Commit-1 deterministic post-filter runs
/// immediately after as a backstop — document-structure noise cannot reach resolution.
///
/// Determinism: all calls go through <see cref="IKozmoLlm"/>. In replay mode this is
/// a <see cref="Kozmo.Llm.CachingLlmClient"/> that serves from a frozen cassette and
/// throws <see cref="LlmCacheMissException"/> on a miss. The recorder tool wraps it with
/// the real OpenAI client to populate cassettes; tests always replay offline.
/// </summary>
public sealed class DocumentCandidateExtractor
{
    private readonly IKozmoLlm       _llm;
    private readonly CandidateFilter _filter;

    public DocumentCandidateExtractor(IKozmoLlm llm, CandidateFilterConfig? config = null)
    {
        _llm    = llm;
        _filter = new CandidateFilter(config);
    }

    /// <summary>
    /// Extracts candidate parties from <paramref name="documentText"/> via one LLM call.
    /// Runs the deterministic post-filter on every returned name before building beliefs.
    /// </summary>
    /// <param name="documentText">Full extracted text of the document (pre-extracted by PdfTextExtractor).</param>
    /// <param name="docId">Filename or stable identifier used as provenance.DocId.</param>
    /// <param name="tier">Source tier implied by doc type (use <see cref="DocTypeInferrer.InferTier"/>).</param>
    /// <param name="isBankingContext">
    /// True when the document is an ACH/banking-details/wire-instruction form (use
    /// <see cref="DocTypeInferrer.IsBankingContext"/>) — B3: tells the extraction prompt that any
    /// bank/financial institution named in it is a payment-routing detail, not a vendor, so the
    /// model reliably classifies it role="issuer" instead of "unknown". Defaults to false so every
    /// existing caller that doesn't compute this signal is unaffected.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// Filtered, deduped list of <see cref="CandidateIdentityBelief"/> records ready for resolution.
    /// Empty if the LLM returns no valid parties. Throws <see cref="LlmCacheMissException"/>
    /// in replay mode when the cassette has no entry for this document.
    /// </returns>
    public async Task<IReadOnlyList<CandidateIdentityBelief>> ExtractAsync(
        string            documentText,
        string            docId,
        SourceTier        tier,
        bool              isBankingContext = false,
        CancellationToken ct = default)
    {
        var system = ExtractionPrompt.System;
        var user   = ExtractionPrompt.User(documentText, isBankingContext);

        var result = await _llm.CompleteJsonAsync(system, user, ExtractionPrompt.MaxTokens, ct);

        return ParseAndFilter(result, docId, tier);
    }

    // ── Parsing ────────────────────────────────────────────────────────────────

    private IReadOnlyList<CandidateIdentityBelief> ParseAndFilter(
        LlmResult result, string docId, SourceTier tier)
    {
        if (result.Answer is not JsonElement root)
            return Array.Empty<CandidateIdentityBelief>();

        // Base confidence: use the JSON-level confidence if present, else LlmResult.Confidence.
        double baseConf = result.Confidence > 0 ? result.Confidence : 0.85;
        if (root.TryGetProperty("confidence", out var confProp) &&
            confProp.ValueKind == JsonValueKind.Number)
        {
            var jsonConf = confProp.GetDouble();
            if (jsonConf > 0) baseConf = jsonConf;
        }
        double clampedConf = Math.Min(baseConf, DocTypeInferrer.TierCeiling(tier));

        if (!root.TryGetProperty("parties", out var partiesEl) ||
            partiesEl.ValueKind != JsonValueKind.Array)
            return Array.Empty<CandidateIdentityBelief>();

        var beliefs = new List<CandidateIdentityBelief>();
        var seenCleanedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var party in partiesEl.EnumerateArray())
        {
            var rawName = GetString(party, "name");
            if (string.IsNullOrWhiteSpace(rawName))
                continue;

            var role    = GetString(party, "role") ?? "unknown";
            var domain  = GetNullableString(party, "domain");
            var address = GetNullableString(party, "address");
            var taxId   = GetNullableString(party, "tax_id");

            // Apply the Commit-1 deterministic post-filter.
            var outcome = _filter.Apply(rawName);
            if (outcome.Verdict == FilterVerdict.Dropped)
                continue;

            var cleanedName = outcome.CleanedName!;

            // Dedup by cleaned name within this document.
            if (!seenCleanedNames.Add(cleanedName))
                continue;

            beliefs.Add(new CandidateIdentityBelief(
                CandidateId: Guid.NewGuid(),
                RawName:     cleanedName,
                SourceTier:  tier,
                Confidence:  clampedConf,
                Provenance:  new Provenance(DocId: docId, Page: null, Span: null),
                Signals:     (domain != null || address != null || taxId != null)
                                 ? new CandidateSignals(domain, address, taxId, Country: null)
                                 : null,
                RoleHint:    role
            ));
        }

        return beliefs;
    }

    // ── JSON helpers ───────────────────────────────────────────────────────────

    private static string? GetString(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static string? GetNullableString(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;
}
