using System.Text.Json;
using Kozmo.Contracts;
using Kozmo.Contracts.Config;
using Kozmo.Llm;

namespace Ii.CandidateExtraction;

/// <summary>
/// LLM-based email interpretation (E-signal Part 5 Step 5) — the belief/signal counterpart of
/// <see cref="DocumentBeliefExtractor"/> for a parsed email instead of a document. Two isolated
/// LLM calls per email, mirroring <see cref="DocumentBeliefExtractor"/>'s 1+N architecture (E1
/// Part 7 Step 6's proven isolation): ONE belief pass (the "email" schema's claim keys, at
/// correspondence tier — <see cref="SourceTier.Correspondence"/>, always; email has no
/// per-filename tier the way documents do) plus ONE signal-group pass (relationship intelligence:
/// sentiment, commitment, issue_raised, stakeholder_signal, request). Beliefs and signals reuse
/// <see cref="DocumentBeliefExtractor.ParseBeliefs"/>/<see cref="DocumentBeliefExtractor.ParseMetadata"/>
/// directly — same deterministic guards, same abstain discipline, same parsing — so email can
/// never silently apply a weaker filter than the document path for the same claim keys.
/// <para>
/// <c>responsiveness</c> is NOT produced here — it is computed deterministically from message
/// timestamps and thread structure (Kozmo_Phase_E_Signal_Spec.md Appendix #2), never
/// LLM-interpreted. Deferred to Part 5 Step 7 (thread awareness): the real corpus has no
/// <c>In-Reply-To</c>/<c>References</c> headers (KYV_KNOWN_GAPS.md), so a reliable thread-relative
/// computation needs the Step 7 threading decision first — not invented speculatively here.
/// </para>
/// </summary>
public sealed class EmailInterpretationExtractor
{
    private readonly IKozmoLlm   _llm;
    private readonly SaasProfile _profile;

    // Fallback belief keys / signal fields when the catalogue predates the "email" extraction
    // schema entry — mirrors DocumentBeliefExtractor.ResolveExtractionSchema's defensive pattern
    // (never leave a document/email type without a key set just because a schema is missing).
    private static readonly string[] FallbackBeliefKeys =
        ["payment_terms", "renewal_date", "annual_value", "invoice_amount", "sla_uptime"];
    private static readonly string[] FallbackSignalFields =
        ["sentiment", "commitment", "issue_raised", "stakeholder_signal", "request"];

    public EmailInterpretationExtractor(IKozmoLlm llm, SaasProfile profile)
    {
        _llm     = llm;
        _profile = profile;
    }

    /// <summary>
    /// Extracts beliefs (one isolated LLM call, correspondence tier) and signals (one LLM call per
    /// declared signal group, unioned) from <paramref name="email"/>. Either list is empty if the
    /// email states none of its schema's targets — the expected, correct result for most routine
    /// correspondence. Throws <see cref="LlmCacheMissException"/> in replay mode when the belief
    /// pass's cassette entry is missing; a signal group's cache miss degrades only that group to
    /// empty (same tolerance as <see cref="DocumentBeliefExtractor"/>).
    /// </summary>
    public async Task<ExtractionResult> ExtractAsync(ParsedEmail email, CancellationToken ct = default)
    {
        var (beliefKeys, signalGroups) = ResolveEmailSchema();

        // ── Belief pass — isolated, correspondence tier always ──────────────────────────
        var beliefSystem = EmailInterpretationPrompt.BuildBeliefSystem(_profile.ClaimKeyCatalogue, beliefKeys);
        var beliefUser   = EmailInterpretationPrompt.User(email.Body);
        var beliefResult = await _llm.CompleteJsonAsync(
            beliefSystem, beliefUser, EmailInterpretationPrompt.BeliefMaxTokens, ct);

        var beliefs = beliefResult.Answer is JsonElement beliefRoot
            ? DocumentBeliefExtractor.ParseBeliefs(
                beliefRoot, beliefResult.Confidence, email.FileName, SourceTier.Correspondence,
                beliefKeys, _profile.ClaimKeyCatalogue, sourcePrefix: "email:")
            : Array.Empty<BeliefCandidate>();

        // ── Signal passes — one call per declared group, unioned ────────────────────────
        var signals = new List<MetadataCandidate>();
        foreach (var groupFields in signalGroups)
        {
            var signalSystem = EmailInterpretationPrompt.BuildSignalSystem(_profile.MetadataFieldCatalogue, groupFields);
            var signalUser   = EmailInterpretationPrompt.User(email.Body);

            LlmResult signalResult;
            try
            {
                signalResult = await _llm.CompleteJsonAsync(
                    signalSystem, signalUser, EmailInterpretationPrompt.SignalMaxTokens, ct);
            }
            catch (LlmCacheMissException)
            {
                continue; // this group's cassette entry is missing — degrade to empty
            }

            if (signalResult.Answer is JsonElement signalRoot)
                signals.AddRange(DocumentBeliefExtractor.ParseMetadata(
                    signalRoot, email.FileName, groupFields, sourcePrefix: "email:"));
        }

        return new ExtractionResult(beliefs, signals);
    }

    /// <summary>
    /// Reads the "email" entry from <see cref="SaasProfile.ExtractionSchemas"/> (E-signal Part 5
    /// Step 4). Falls back to the hardcoded key/field sets above if the catalogue predates that
    /// entry — a config change, never a code change, extends this the same way a new document
    /// type's schema does for <see cref="DocumentBeliefExtractor"/>.
    /// </summary>
    private (IReadOnlyList<string> BeliefKeys, IReadOnlyList<IReadOnlyList<string>> SignalGroups) ResolveEmailSchema()
    {
        if (_profile.ExtractionSchemas.TryGetValue("email", out var schema) && schema.BeliefKeys.Count > 0)
        {
            var groups = schema.MetadataFieldGroups.Select(g => (IReadOnlyList<string>)g.Fields).ToList();
            return (schema.BeliefKeys, groups);
        }

        return (FallbackBeliefKeys, new IReadOnlyList<string>[] { FallbackSignalFields });
    }
}
