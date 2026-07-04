using System.Text;
using Kozmo.Contracts.Config;

namespace Ii.CandidateExtraction;

/// <summary>
/// System prompt and user-prompt builder for the dimension-fact belief extraction LLM call.
/// Tunable: changing <see cref="BuildSystem"/>'s output for a given key set causes that key set's
/// cassette keys to change (SHA-256 includes the system prompt), so a cassette re-record pass is
/// required after any change that actually alters the generated text for that key set.
/// <para>
/// E1 Part 7 Step 2: the system prompt is generated from
/// <c>catalogue/profiles/saas/claim_key_catalogue.saas.v1.json</c> instead of hand-authored as a
/// prose constant. Each target key's <c>prompt_fragment</c> catalogue field holds the original
/// hand-authored wording byte-for-byte (not the <c>definition</c>/<c>positive_example</c>/
/// <c>negative_example</c> fields, which are a separate, deliberately-paraphrased documentation
/// layer). Calling <see cref="BuildSystem"/> with <see cref="TargetCriteriaOrder"/> (the pre-E1
/// five-key set) reproduces the original prompt string exactly — proven by
/// <c>BeliefExtractionPromptGenerationTests</c> asserting hash/string equality against the original
/// constant, offline.
/// </para>
/// <para>
/// E1 Part 7 Step 3: <see cref="BuildSystem"/> now takes an explicit ordered key list instead of
/// always using <see cref="TargetCriteriaOrder"/>, so <see cref="DocumentBeliefExtractor"/> can
/// project a document-type-specific subset (<c>SaasProfile.ExtractionSchemas</c>) — e.g. an invoice
/// asks about <c>invoice_amount</c> instead of <c>annual_value</c>. <see cref="TargetCriteriaOrder"/>
/// remains the default/fallback key set for unclassified document types.
/// </para>
/// <para>
/// A claim key's <c>deterministic_guard</c> catalogue field is documentation only — it names which
/// method in <see cref="DocumentBeliefExtractor"/> enforces that key's hard rule in code. It is
/// never projected into the generated prompt and never used to derive guard behavior; the guards
/// stay hand-written in <see cref="DocumentBeliefExtractor"/>.
/// </para>
/// </summary>
public static class BeliefExtractionPrompt
{
    /// <summary>Max tokens for the extraction response. Five short facts plus reasoning.</summary>
    public const int MaxTokens = 800;

    /// <summary>Max characters of document text included in the user prompt.</summary>
    private const int MaxDocChars = 15_000;

    /// <summary>
    /// The default/fallback criterion keys, in the fixed order they are projected into the
    /// generated prompt (matches the original hand-authored bullet order exactly). Used for any
    /// document type with no entry in <c>SaasProfile.ExtractionSchemas</c>.
    /// </summary>
    public static readonly IReadOnlyList<string> TargetCriteriaOrder = new[]
    {
        "sla_uptime", "csat", "payment_terms", "renewal_date", "annual_value"
    };

    /// <summary>
    /// Builds the extraction system prompt by concatenating fixed boilerplate with each key in
    /// <paramref name="targetCriteriaOrder"/>'s catalogue <c>prompt_fragment</c> (the original
    /// wording, verbatim, for pre-E1 keys). Deterministic for a given catalogue and key list — same
    /// inputs always yield the same string. <see cref="DocumentBeliefExtractor"/> drops any fact
    /// the model returns outside <paramref name="targetCriteriaOrder"/>, even if well-formed.
    /// </summary>
    public static string BuildSystem(
        IReadOnlyDictionary<string, ClaimKeyDefinition> catalogue,
        IReadOnlyList<string> targetCriteriaOrder)
    {
        var sb = new StringBuilder();
        void Line(string s = "") => sb.Append(s).Append('\n');

        Line("You are a strict commercial-intelligence fact extractor. Extract ONLY the five facts");
        Line("listed below, and ONLY when a value is EXPLICITLY stated in the document text. Do NOT");
        Line("infer, estimate, approximate, or use outside knowledge.");
        Line();
        Line("FACTS (criterion key -> what to look for -> raw value encoding):");

        foreach (var key in targetCriteriaOrder)
        {
            if (!catalogue.TryGetValue(key, out var def) || string.IsNullOrEmpty(def.PromptFragment))
                throw new InvalidOperationException(
                    $"BeliefExtractionPrompt.BuildSystem: target criterion '{key}' has no " +
                    "prompt_fragment in the claim key catalogue.");

            Line(def.PromptFragment);
        }

        Line();
        Line("ABSTENTION IS MANDATORY: if a fact is not explicitly present in the document, DO NOT");
        Line("include it in the output — omit it entirely. Never invent, guess, or infer a value that");
        Line("is not stated. If NONE of the five facts appear anywhere in the document, return an");
        Line("empty \"facts\" array. An empty array is a correct, expected answer for many documents");
        Line("(e.g. marketing brochures, meeting notes) — it is not a failure.");
        Line();
        Line("Every fact you DO include must carry the exact quoted span of text it was drawn from, so");
        Line("the claim can be checked against the source.");
        Line();
        Line("Return JSON with this exact shape — no markdown fences, just the JSON object:");
        Line("{");
        Line("  \"facts\": [");
        Line("    {");
        Line($"      \"criterion\": \"<{string.Join("|", targetCriteriaOrder)}>\",");
        Line("      \"value\": <number, EXCEPT renewal_date which is the string \"YYYY-MM-DD\">,");
        Line("      \"evidence\": \"<exact quoted span from the document text>\",");
        Line("      \"confidence\": <float 0.0-1.0>");
        Line("    }");
        Line("  ],");
        Line("  \"confidence\": <float 0.0-1.0>,");
        Line("  \"reasoning\": \"<one sentence>\"");
        sb.Append('}');

        return sb.ToString();
    }

    public static string User(string documentText)
    {
        var text = documentText.Length > MaxDocChars
            ? documentText[..MaxDocChars] + "\n[... truncated ...]"
            : documentText;
        return $"Document text:\n\n{text}";
    }
}
