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
/// One retained, non-scored metadata fact candidate (E1 Part 7 Step 5) — e.g. a termination
/// clause or a liability cap, extracted from a metadata-GROUP LLM pass (E1 Part 7 Step 6) but
/// routed to the metadata store (<c>Km.Store.Metadata</c>), never to <c>IEntityStore</c>.
/// Deliberately does not reference any <c>Km.Store</c> type — <c>Ii.CandidateExtraction</c> stays
/// behind the metadata wall the same way it stays free of any scoring-assembly dependency; the
/// conversion to <c>Km.Store.Metadata.DocumentMetadata</c> happens downstream, in
/// <c>Kyv.ProgramRunner</c>'s persistence stage.
/// </summary>
public sealed record MetadataCandidate(
    string FieldName,
    string Value,
    string Derivation
);

/// <summary>
/// One document's full extraction result — beliefs (scored, feed Rubric/Index, from ONE isolated
/// LLM pass) and metadata (retained, never scored, unioned across N per-group LLM passes — E1
/// Part 7 Step 6). A schema with no metadata field groups (every document type except MSA today)
/// always yields an empty <see cref="Metadata"/> list and makes no metadata LLM calls at all.
/// </summary>
public sealed record ExtractionResult(
    IReadOnlyList<BeliefCandidate>   Beliefs,
    IReadOnlyList<MetadataCandidate> Metadata
);

/// <summary>
/// LLM-based dimension-fact extractor (belief bridge, Commit 1; document-type-aware schemas,
/// E1 Part 7 Step 3; metadata extraction, E1 Part 7 Step 5; multi-pass metadata groups,
/// E1 Part 7 Step 6).
///
/// Makes 1 + N LLM calls per document: ONE isolated belief-extraction pass (the document type's
/// schema-selected belief facts — <see cref="ResolveExtractionSchema"/>; the pre-E1 default is
/// sla_uptime, csat, payment_terms, renewal_date, annual_value) plus ONE pass per declared
/// metadata field GROUP (~4-5 thematically-related fields each). N is 0 for any document type
/// with no metadata groups declared (every type except MSA today) — those documents make exactly
/// the same single belief call as before Step 5/6 ever existed, same cache key, same behavior.
///
/// Step 5 tried combining beliefs and all of a type's metadata fields into ONE call. Real-corpus
/// proof showed near-zero metadata recall even on documents that verifiably state most fields —
/// GPT-4o-mini can't reliably hold 18 categories (plus 5 belief facts) in attention at once, and
/// the composition change also risked rippling into belief extraction (Step 3's lesson recurring).
/// Splitting metadata into small, isolated, thematically-coherent group passes fixed both:
/// recall jumped from 0/18 to double digits on the richest real document, and the belief pass —
/// now a completely separate call, never combined with any metadata prompt — is byte-identical
/// for a given key list regardless of how many metadata groups the document type declares.
///
/// Abstention is the default throughout: a document with none of a pass's targets yields an empty
/// list for that pass, not a guess. A metadata-group cache miss degrades that group to empty
/// rather than failing the whole extraction — beliefs and other groups are unaffected. A
/// belief-pass cache miss still throws <see cref="LlmCacheMissException"/>, exactly as before.
///
/// Determinism: all calls go through <see cref="IKozmoLlm"/>. In replay mode this is a
/// <see cref="Kozmo.Llm.CachingLlmClient"/> that serves from a frozen cassette. The recorder tool
/// (Kyv.BeliefRecorder) wraps it with the real OpenAI client to populate cassettes; tests always
/// replay offline.
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
    /// Extracts beliefs (one isolated LLM call) and metadata (one LLM call per declared field
    /// group, unioned) from <paramref name="documentText"/>.
    /// </summary>
    /// <param name="documentText">Full extracted text of the document.</param>
    /// <param name="docId">Filename or stable identifier, recorded in Derivation for provenance.</param>
    /// <param name="tier">Source tier implied by doc type (use <see cref="DocTypeInferrer.InferTier"/>).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// Beliefs and metadata explicitly present in the document. Either list is empty if none of
    /// its schema's targets appear — this is the expected, correct result for most documents.
    /// Throws <see cref="LlmCacheMissException"/> in replay mode when the belief pass's cassette
    /// entry is missing; a metadata group's cache miss degrades only that group to empty.
    /// </returns>
    public async Task<ExtractionResult> ExtractAsync(
        string            documentText,
        string            docId,
        SourceTier        tier,
        CancellationToken ct = default)
    {
        var schema = ResolveExtractionSchema(docId);

        // ── Belief pass — isolated, always this exact shape regardless of metadata depth ──
        var beliefSystem = BeliefExtractionPrompt.BuildSystem(_profile.ClaimKeyCatalogue, schema.BeliefKeys);
        var beliefUser   = BeliefExtractionPrompt.User(documentText);
        var beliefResult = await _llm.CompleteJsonAsync(beliefSystem, beliefUser, BeliefExtractionPrompt.MaxTokens, ct);

        var beliefs = beliefResult.Answer is JsonElement beliefRoot
            ? ParseBeliefs(beliefRoot, beliefResult.Confidence, docId, tier, schema.BeliefKeys)
            : Array.Empty<BeliefCandidate>();

        // ── Metadata passes — one call per declared group, unioned ──────────────────────
        var metadata = new List<MetadataCandidate>();
        foreach (var group in schema.MetadataFieldGroups)
        {
            var metaSystem = BeliefExtractionPrompt.BuildMetadataGroupSystem(_profile.MetadataFieldCatalogue, group.Fields);
            var metaUser   = BeliefExtractionPrompt.User(documentText, BeliefExtractionPrompt.MaxDocCharsWithMetadata);

            LlmResult metaResult;
            try
            {
                metaResult = await _llm.CompleteJsonAsync(metaSystem, metaUser, BeliefExtractionPrompt.MetadataGroupMaxTokens, ct);
            }
            catch (LlmCacheMissException)
            {
                continue; // this group's cassette entry is missing — degrade to empty, not a whole-document failure
            }

            if (metaResult.Answer is JsonElement metaRoot)
                metadata.AddRange(ParseMetadata(metaRoot, docId, group.Fields));
        }

        return new ExtractionResult(beliefs, metadata);
    }

    /// <summary>
    /// E1 Part 7 Step 3/5/6: selects the document type's extraction schema (belief keys + metadata
    /// field groups), based on the document's inferred type (<see cref="DocTypeInferrer.InferDocType"/>).
    /// Falls back to <see cref="SaasProfile.DefaultExtractionSchema"/> — and if that has no belief
    /// keys (catalogue predates the extraction_schemas config), to
    /// <see cref="BeliefExtractionPrompt.TargetCriteriaOrder"/> with no metadata groups — for any
    /// document type with no schema entry, so adding a schema (or a metadata group) is a config
    /// change, not a code change, and unmapped types are never left without a key set.
    /// </summary>
    private ExtractionSchema ResolveExtractionSchema(string docId)
    {
        var docType = DocTypeInferrer.InferDocType(docId);
        if (docType.Length > 0 && _profile.ExtractionSchemas.TryGetValue(docType, out var schema))
            return schema;

        return _profile.DefaultExtractionSchema.BeliefKeys.Count > 0
            ? _profile.DefaultExtractionSchema
            : new ExtractionSchema(BeliefExtractionPrompt.TargetCriteriaOrder, Array.Empty<MetadataFieldGroup>());
    }

    // ── Belief parsing ─────────────────────────────────────────────────────────

    private IReadOnlyList<BeliefCandidate> ParseBeliefs(
        JsonElement root, double resultConfidence, string docId, SourceTier tier, IReadOnlyList<string> targetKeys)
    {
        if (!root.TryGetProperty("facts", out var factsEl) || factsEl.ValueKind != JsonValueKind.Array)
            return Array.Empty<BeliefCandidate>();

        var targetSet  = new HashSet<string>(targetKeys, StringComparer.OrdinalIgnoreCase);
        var ceiling    = DocTypeInferrer.TierCeiling(tier);
        var candidates = new List<BeliefCandidate>();
        var seen       = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var fact in factsEl.EnumerateArray())
        {
            var criterion = GetString(fact, "criterion");
            if (string.IsNullOrWhiteSpace(criterion))
                continue;

            // Only catalogue-known, in-scope criteria for THIS document's schema — everything
            // else is dropped even if the model returns a well-formed object for it.
            if (!targetSet.Contains(criterion))
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

            // Narrow backstop for the same prompt rule (BeliefExtractionPrompt.System's csat
            // negative example): reject the two specific non-customer quality metrics a real
            // document confused with CSAT ("study quality scores" — see KYV_KNOWN_GAPS.md).
            // Deliberately narrow, not a general "must mention customer/satisfaction" filter —
            // that would risk rejecting legitimate CSAT evidence phrased without those exact
            // words. The prompt tightening is the primary fix; this only catches the specific
            // confusion already proven to happen.
            if (string.Equals(criterion, "csat", StringComparison.OrdinalIgnoreCase) &&
                ContainsNonCustomerQualityLanguage(evidence!))
                continue;

            // E1 Part 7 Step 3 — annual_value periodicity guard (the deferred fix from
            // KYV_KNOWN_GAPS.md's IIVS investigation): the invoice extraction schema now asks
            // about invoice_amount instead of annual_value, which fixes the confusion at the
            // root for documents classified as invoices. This is defense-in-depth for any other
            // document type still asking about annual_value — a milestone/one-time payment line
            // (e.g. an invoice table embedded in a non-invoice-classified document) never becomes
            // an annual_value belief, regardless of what the model returned.
            if (string.Equals(criterion, "annual_value", StringComparison.OrdinalIgnoreCase) &&
                ContainsMilestoneLanguage(evidence!))
                continue;

            // E1 Part 7 Step 5 — annual_value insurance/liability guard: adding MSA metadata
            // fields (insurance_requirements, liability_cap) surfaced a NEW real confusion on the
            // real IIVS MSA — a Commercial General Liability Insurance policy limit
            // ("not less than $1,000,000") got extracted as annual_value, exactly the "Insurance
            // requirements... are NOT fees" case the prompt's own negative example already warns
            // against, in prose the model still doesn't reliably follow. Same class of fix as
            // ContainsMilestoneLanguage: enforced in code, not just prose.
            if (string.Equals(criterion, "annual_value", StringComparison.OrdinalIgnoreCase) &&
                ContainsInsuranceOrLiabilityLanguage(evidence!))
                continue;

            var rawConf = fact.TryGetProperty("confidence", out var cf) && cf.ValueKind == JsonValueKind.Number
                ? cf.GetDouble()
                : resultConfidence;
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

    // ── Metadata parsing (E1 Part 7 Step 5) ─────────────────────────────────────

    /// <summary>
    /// Parses the "metadata" JSON array into <see cref="MetadataCandidate"/>s, filtered to
    /// <paramref name="targetFields"/> — the same abstain-and-filter discipline as
    /// <see cref="ParseBeliefs"/>, minus the belief-specific guards and value/date parsing
    /// (metadata values are free text, not scored magnitudes). Returns empty immediately when
    /// the schema requests no metadata fields — no "metadata" key is even expected in that case.
    /// </summary>
    private IReadOnlyList<MetadataCandidate> ParseMetadata(
        JsonElement root, string docId, IReadOnlyList<string> targetFields)
    {
        if (targetFields.Count == 0)
            return Array.Empty<MetadataCandidate>();

        if (!root.TryGetProperty("metadata", out var metaEl) || metaEl.ValueKind != JsonValueKind.Array)
            return Array.Empty<MetadataCandidate>();

        var targetSet = new HashSet<string>(targetFields, StringComparer.OrdinalIgnoreCase);
        var seen      = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var results   = new List<MetadataCandidate>();

        foreach (var item in metaEl.EnumerateArray())
        {
            var field = GetString(item, "field");
            if (string.IsNullOrWhiteSpace(field))
                continue;

            // Only catalogue-known, in-scope fields for THIS document's schema — everything else
            // is dropped even if the model returns a well-formed object for it.
            if (!targetSet.Contains(field))
                continue;

            // One value per field per document — mirrors the belief-parsing convention.
            if (!seen.Add(field))
                continue;

            var value = GetString(item, "value");
            if (string.IsNullOrWhiteSpace(value))
                continue; // no concrete value — abstain on this field

            var evidence = GetString(item, "evidence");
            if (string.IsNullOrWhiteSpace(evidence))
                continue; // ungrounded field — abstain rather than trust an uncited value

            results.Add(new MetadataCandidate(
                FieldName:  field,
                Value:      value!,
                Derivation: $"doc:{docId} \"{Truncate(evidence!, 200)}\""));
        }

        return results;
    }

    // ── payment_terms termination-notice guard ─────────────────────────────────

    // Substrings that indicate the quoted span is about a termination/cancellation/insurance
    // notice period rather than an invoice payment due date — exactly the exclusion
    // BeliefExtractionPrompt.System already states, enforced here deterministically.
    private static readonly string[] TerminationLanguage =
        ["terminat", "cancel", "notice"];

    private static bool ContainsTerminationLanguage(string evidence) =>
        TerminationLanguage.Any(kw => evidence.Contains(kw, StringComparison.OrdinalIgnoreCase));

    // ── csat non-customer-quality guard ─────────────────────────────────────────

    // The exact negative-example phrases from BeliefExtractionPrompt.System's csat rule —
    // narrow by design (see comment at the call site above).
    private static readonly string[] NonCustomerQualityLanguage =
        ["study quality", "product quality"];

    private static bool ContainsNonCustomerQualityLanguage(string evidence) =>
        NonCustomerQualityLanguage.Any(kw => evidence.Contains(kw, StringComparison.OrdinalIgnoreCase));

    // ── annual_value periodicity/milestone guard ────────────────────────────────

    // Narrow by design, matching the one proven real-document confusion (IIVS's per-engagement
    // invoices: "Milestone M2 -- SOW-01" — see KYV_KNOWN_GAPS.md) — not a general "any dollar
    // figure near billing language" filter, which would risk rejecting legitimate annual_value
    // evidence phrased with adjacent invoice/billing context.
    private static readonly string[] MilestoneLanguage =
        ["milestone"];

    private static bool ContainsMilestoneLanguage(string evidence) =>
        MilestoneLanguage.Any(kw => evidence.Contains(kw, StringComparison.OrdinalIgnoreCase));

    // ── annual_value insurance/liability guard (E1 Part 7 Step 5) ───────────────

    // Matches the prompt's own annual_value negative example ("Insurance requirements, liability
    // caps, and indemnification ceilings are NOT fees") — narrow to the proven real confusion
    // (a CGL policy limit read as annual_value on the real IIVS MSA), not a general "any dollar
    // figure near legal language" filter.
    private static readonly string[] InsuranceOrLiabilityLanguage =
        ["insurance", "liability", "indemnif"];

    private static bool ContainsInsuranceOrLiabilityLanguage(string evidence) =>
        InsuranceOrLiabilityLanguage.Any(kw => evidence.Contains(kw, StringComparison.OrdinalIgnoreCase));

    // ── JSON helpers ───────────────────────────────────────────────────────────

    private static string? GetString(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "...";
}
