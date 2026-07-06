using System.Text.Json;
using System.Text.Json.Serialization;
using Kozmo.Contracts;
using Kozmo.Contracts.Config;

namespace Ii.Completeness;

/// <summary>
/// System prompt and user-prompt builder for the completeness-question answering LLM call.
/// Changing System invalidates all cassette keys for the answering stage — a re-record pass
/// is required after every prompt edit (same constraint as ExtractionPrompt).
/// </summary>
public static class AnsweringPrompt
{
    /// <summary>Max tokens for the answering response. Covers a concise cited answer.</summary>
    public const int MaxTokens = 600;

    /// <summary>
    /// Max characters of serialized belief JSON included in the user prompt.
    /// Analogous to ExtractionPrompt.MaxDocChars — caps token spend and keeps the cassette
    /// key stable when a vendor's belief set grows beyond a reasonable evidence window.
    /// </summary>
    public const int MaxBeliefChars = 15_000;

    public const string System = """
        You are a commercial-intelligence completeness assessor. Given a completeness question
        about a vendor and a set of structured evidence (beliefs), reason from the evidence to
        produce a grounded answer.

        GROUNDING RULES — follow exactly:
        1. Your answer MUST be grounded in the provided beliefs. In "cited_belief_ids", list the
           "id" value of every belief you drew on. If you used no beliefs, the list must be empty
           and the answer must be UNKNOWN.
        2. UNKNOWN discipline: when the beliefs do not contain enough information to answer the
           question, set "answer" to "UNKNOWN" and "confidence" to ≤ 0.30. Do NOT fabricate,
           assume, or infer beyond what the beliefs state. UNKNOWN is a first-class, correct
           response — it flags a real gap.
        3. Confidence reflects evidence weight: Primary-tier beliefs → higher confidence;
           Reported/Inferred → moderate; Unverified or no relevant beliefs → ≤ 0.30.

        Return JSON with this exact shape — no markdown fences, just the JSON object:
        {
          "answer": "<YES|NO|<value>|UNKNOWN>",
          "confidence": <float 0.0–1.0>,
          "cited_belief_ids": ["<id of each belief used>"],
          "reasoning": "<one sentence>"
        }
        """;

    public static string User(Question question, IReadOnlyList<Belief> beliefs, SaasProfile? profile = null)
    {
        var beliefJson = SerializeBeliefs(beliefs, profile);
        if (beliefJson.Length > MaxBeliefChars)
            beliefJson = beliefJson[..MaxBeliefChars] + "\n  ... [truncated to fit context window]";

        return
            $"Question (id: {question.Id}, type: {question.AnswerType}):\n" +
            $"{question.Text}\n\n" +
            $"Vendor beliefs ({beliefs.Count} item(s)):\n" +
            beliefJson;
    }

    /// <summary>
    /// Deterministic prompt order for a belief list — sorted by Criterion then Derivation, the
    /// same ordering <c>RealVendorBeliefFixture</c> already used to remap ids for cassette
    /// stability. Promoted here so the ordinal labels in <see cref="SerializeBeliefs"/> and the
    /// ordinal→real-id map <see cref="QuestionAnsweringStage"/> builds for citation translation
    /// are guaranteed to agree — both are computed by calling this same method.
    /// </summary>
    public static IReadOnlyList<Belief> OrderForPrompt(IReadOnlyList<Belief> beliefs) =>
        beliefs
            .OrderBy(b => b.Criterion,  StringComparer.OrdinalIgnoreCase)
            .ThenBy(b => b.Derivation,  StringComparer.OrdinalIgnoreCase)
            .ToList();

    // Stable, minimal JSON — only the fields the LLM needs to reason and cite.
    // "Id" is a small 1-based ORDINAL, not the real belief.Id — belief.Id is Guid.NewGuid() at
    // persistence time, so embedding it here made every genuine run's prompt (and therefore its
    // cassette key) different from every other run, even for byte-identical evidence. The
    // ordinal is stable across runs for the same belief set (OrderForPrompt is deterministic),
    // so the SAME evidence always produces the SAME prompt regardless of the random ids beneath
    // it. QuestionAnsweringStage translates cited ordinals back to real belief ids afterward.
    internal static string SerializeBeliefs(IReadOnlyList<Belief> beliefs, SaasProfile? profile = null)
    {
        var items = OrderForPrompt(beliefs)
            .Select((b, i) => new BeliefView(
                (i + 1).ToString(),
                b.Dimension?.ToString() ?? "Unknown",
                b.Criterion,
                b.SourceTier.ToString(),
                PresentationConfidence(b, profile),
                b.Derivation));

        return JsonSerializer.Serialize(items, PromptJsonOpts);
    }

    // Presentation-only evidence weight shown to THIS prompt — never the persisted
    // Belief.Confidence or anything RubricModule sees. VendorFileWriteService intentionally
    // zeroes Confidence for structural claims (payment_terms, annual_value, renewal_date, ...)
    // so they never pollute RubricModule's scoring average — correct for scoring. But this
    // prompt's System text tells the LLM "confidence reflects evidence weight ... no relevant
    // beliefs -> <= 0.30", and a literal 0.0 reads as "no evidence", suppressing grounded
    // answers for structural facts that genuinely exist. Substituting the belief's own
    // source-tier ceiling (the same trust ceiling the store itself uses for scored beliefs)
    // gives the LLM an honest, non-zero evidence weight without touching the scoring path.
    // See KYV_KNOWN_GAPS.md — a real evidence-weight vs scoring-weight split in the belief
    // model is the proper long-term fix; this is the presentation-only stopgap.
    private static double PresentationConfidence(Belief belief, SaasProfile? profile)
    {
        if (belief.Confidence > 0) return belief.Confidence;

        if (profile != null && profile.SourceTiers.TryGetValue(belief.SourceTier.ToString(), out var tierConfig))
            return tierConfig.Ceiling;

        return FallbackTierCeiling(belief.SourceTier);
    }

    // Used only when no profile is supplied — mirrors catalogue/profiles/saas/source_tiers.saas.v1.json.
    private static double FallbackTierCeiling(SourceTier tier) => tier switch
    {
        SourceTier.Primary        => 1.0,
        SourceTier.Verified       => 0.8,
        SourceTier.Reported       => 0.5,
        SourceTier.Inferred       => 0.3,
        SourceTier.Unverified     => 0.2,
        SourceTier.Correspondence => 0.25,
        SourceTier.Confirmed      => 0.65,
        _                         => 0.5,
    };

    private static readonly JsonSerializerOptions PromptJsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented        = true,
    };

    // Sealed to prevent accidental mutation in the prompt serialization path.
    private sealed record BeliefView(
        string Id,
        string Dimension,
        string Criterion,
        string SourceTier,
        double Confidence,
        string Derivation);
}
