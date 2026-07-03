using System.Globalization;
using System.Text.Json;
using Kozmo.Contracts;
using Kozmo.Contracts.Config;
using Kozmo.Llm;

namespace Ii.CandidateExtraction;

/// <summary>
/// One dimension-fact candidate extracted from a document, ready for a later persistence stage
/// (e.g. <c>VendorFileWriteService</c>) to write as a <c>Belief</c>.
/// <para>
/// <see cref="Value"/> is the RAW magnitude as stated in the document — e.g. 99.9 for a
/// "99.9% uptime SLA", 4.6 for a "4.6/5.0" CSAT score, a Unix timestamp for
/// <c>renewal_date</c> — NOT a 0-1 normalised rubric score. Normalisation against the scoring
/// rubric is a downstream concern, not this extractor's job. The renewal_date timestamp is
/// computed deterministically in <see cref="DocumentBeliefExtractor"/> from the model's plain
/// "YYYY-MM-DD" string — the model is never asked to do the date arithmetic itself.
/// </para>
/// <para>
/// <see cref="Dimension"/> is null for structural claims (e.g. <c>renewal_date</c>, which
/// carries an empty dimension in the claim_key catalogue) — such candidates must never feed
/// <c>Ii.Rubric</c>.
/// </para>
/// </summary>
public sealed record BeliefCandidate(
    Dimension?  Dimension,
    string      Criterion,
    double      Value,
    double      Confidence,
    SourceTier  SourceTier,
    string      Derivation
);

/// <summary>
/// LLM-based dimension-fact extractor (belief bridge, Commit 1).
///
/// One LLM read per document returns the subset of five catalogue-defined facts
/// (sla_uptime, csat, payment_terms, renewal_date, annual_value) that are explicitly stated in
/// the text. Abstention is the default: a document with none of these facts yields an empty
/// list, not a guess.
///
/// Determinism: all calls go through <see cref="IKozmoLlm"/>. In replay mode this is a
/// <see cref="Kozmo.Llm.CachingLlmClient"/> that serves from a frozen cassette and throws
/// <see cref="LlmCacheMissException"/> on a miss. The recorder tool (Kyv.BeliefRecorder) wraps
/// it with the real OpenAI client to populate cassettes; tests always replay offline.
/// </summary>
public sealed class DocumentBeliefExtractor
{
    private readonly IKozmoLlm   _llm;
    private readonly SaasProfile _profile;

    public DocumentBeliefExtractor(IKozmoLlm llm, SaasProfile profile)
    {
        _llm     = llm;
        _profile = profile;
    }

    /// <summary>
    /// Extracts dimension-fact candidates from <paramref name="documentText"/> via one LLM call.
    /// </summary>
    /// <param name="documentText">Full extracted text of the document.</param>
    /// <param name="docId">Filename or stable identifier, recorded in Derivation for provenance.</param>
    /// <param name="tier">Source tier implied by doc type (use <see cref="DocTypeInferrer.InferTier"/>).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// Candidates for facts explicitly present in the document. Empty if none of the five
    /// target facts appear — this is the expected, correct result for most documents. Throws
    /// <see cref="LlmCacheMissException"/> in replay mode when the cassette has no entry.
    /// </returns>
    public async Task<IReadOnlyList<BeliefCandidate>> ExtractAsync(
        string            documentText,
        string            docId,
        SourceTier        tier,
        CancellationToken ct = default)
    {
        var system = BeliefExtractionPrompt.System;
        var user   = BeliefExtractionPrompt.User(documentText);

        var result = await _llm.CompleteJsonAsync(system, user, BeliefExtractionPrompt.MaxTokens, ct);

        return ParseAndFilter(result, docId, tier);
    }

    // ── Parsing ────────────────────────────────────────────────────────────────

    private IReadOnlyList<BeliefCandidate> ParseAndFilter(LlmResult result, string docId, SourceTier tier)
    {
        if (result.Answer is not JsonElement root)
            return Array.Empty<BeliefCandidate>();

        if (!root.TryGetProperty("facts", out var factsEl) || factsEl.ValueKind != JsonValueKind.Array)
            return Array.Empty<BeliefCandidate>();

        var ceiling    = DocTypeInferrer.TierCeiling(tier);
        var candidates = new List<BeliefCandidate>();
        var seen       = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var fact in factsEl.EnumerateArray())
        {
            var criterion = GetString(fact, "criterion");
            if (string.IsNullOrWhiteSpace(criterion))
                continue;

            // Only catalogue-known, in-scope criteria — everything else is dropped even if
            // the model returns a well-formed object for it.
            if (!BeliefExtractionPrompt.TargetCriteria.Contains(criterion))
                continue;
            if (!_profile.ClaimKeyCatalogue.TryGetValue(criterion, out var ckDef))
                continue;

            // One claim per key per document — mirrors the VendorFilePdfLane convention.
            if (!seen.Add(criterion))
                continue;

            double value;
            if (string.Equals(criterion, "renewal_date", StringComparison.OrdinalIgnoreCase))
            {
                // The model emits a plain "YYYY-MM-DD" string; the epoch conversion happens here,
                // deterministically, rather than asking the model to do date arithmetic itself
                // (LLM-computed timestamps drift — see KYV_KNOWN_GAPS.md).
                if (!fact.TryGetProperty("value", out var dateEl) || dateEl.ValueKind != JsonValueKind.String)
                    continue; // no concrete date string — abstain on this fact

                var dateText = dateEl.GetString();
                if (string.IsNullOrWhiteSpace(dateText) ||
                    !DateTimeOffset.TryParseExact(dateText, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal, out var parsedDate))
                    continue; // unparseable date — abstain rather than guess

                value = parsedDate.ToUnixTimeSeconds();
            }
            else
            {
                if (!fact.TryGetProperty("value", out var valueEl) || valueEl.ValueKind != JsonValueKind.Number)
                    continue; // no concrete numeric value — abstain on this fact

                value = valueEl.GetDouble();
            }

            var evidence = GetString(fact, "evidence");
            if (string.IsNullOrWhiteSpace(evidence))
                continue; // ungrounded fact — abstain rather than trust an uncited value

            // Deterministic guard, not a prompt rule: BeliefExtractionPrompt.System already tells
            // the model "NEVER extract payment_terms from... termination notice periods", and the
            // model still does it occasionally (confirmed on a real Salesforce document — see
            // KYV_KNOWN_GAPS.md). Re-stating the rule more emphatically doesn't reliably fix an
            // instruction the model already had and broke, so this enforces it in code instead —
            // regardless of what the model returned, an evidence span that reads as a
            // termination/cancellation/notice-period clause never becomes a payment_terms belief.
            if (string.Equals(criterion, "payment_terms", StringComparison.OrdinalIgnoreCase) &&
                ContainsTerminationLanguage(evidence!))
                continue;

            var rawConf = fact.TryGetProperty("confidence", out var cf) && cf.ValueKind == JsonValueKind.Number
                ? cf.GetDouble()
                : result.Confidence;
            var confidence = Math.Min(rawConf > 0 ? rawConf : 0.5, ceiling);

            // Structural claims carry an empty dimension in the catalogue (e.g. renewal_date) —
            // Dimension stays null so these candidates never feed Ii.Rubric.
            Dimension? dimension = Enum.TryParse<Dimension>(ckDef.Dimension, ignoreCase: true, out var dim)
                ? dim
                : null;

            candidates.Add(new BeliefCandidate(
                Dimension:  dimension,
                Criterion:  criterion,
                Value:      value,
                Confidence: confidence,
                SourceTier: tier,
                Derivation: $"doc:{docId} \"{Truncate(evidence!, 200)}\""));
        }

        return candidates;
    }

    // ── payment_terms termination-notice guard ─────────────────────────────────

    // Substrings that indicate the quoted span is about a termination/cancellation/insurance
    // notice period rather than an invoice payment due date — exactly the exclusion
    // BeliefExtractionPrompt.System already states, enforced here deterministically.
    private static readonly string[] TerminationLanguage =
        ["terminat", "cancel", "notice"];

    private static bool ContainsTerminationLanguage(string evidence) =>
        TerminationLanguage.Any(kw => evidence.Contains(kw, StringComparison.OrdinalIgnoreCase));

    // ── JSON helpers ───────────────────────────────────────────────────────────

    private static string? GetString(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "...";
}
