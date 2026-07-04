using System.Text;
using Kozmo.Contracts.Config;

namespace Ii.CandidateExtraction;

/// <summary>
/// System prompt and user-prompt builder for the dimension-fact belief extraction LLM call, and
/// for the per-group metadata extraction LLM calls (E1 Part 7 Step 6).
/// Tunable: changing either builder's output for a given key/field set causes that set's
/// cassette keys to change (SHA-256 includes the system prompt), so a cassette re-record pass is
/// required after any change that actually alters the generated text for that set.
/// <para>
/// E1 Part 7 Step 2: <see cref="BuildSystem"/> is generated from
/// <c>catalogue/profiles/saas/claim_key_catalogue.saas.v1.json</c> instead of hand-authored as a
/// prose constant. Each target key's <c>prompt_fragment</c> catalogue field holds the original
/// hand-authored wording byte-for-byte. Calling <see cref="BuildSystem"/> with
/// <see cref="TargetCriteriaOrder"/> (the pre-E1 five-key set) reproduces the original prompt
/// string exactly — proven by <c>BeliefExtractionPromptGenerationTests</c> asserting hash/string
/// equality against the original constant, offline.
/// </para>
/// <para>
/// E1 Part 7 Step 3: <see cref="BuildSystem"/> takes an explicit ordered belief-key list instead
/// of always using <see cref="TargetCriteriaOrder"/>, so <see cref="DocumentBeliefExtractor"/> can
/// project a document-type-specific subset (<c>SaasProfile.ExtractionSchemas</c>).
/// </para>
/// <para>
/// E1 Part 7 Step 5 tried folding metadata fields into this SAME prompt/call, alongside beliefs.
/// Real-corpus proof showed near-zero metadata recall even on documents that verifiably state
/// most fields — asking about 18 metadata categories (plus 5 belief facts) in one pass exceeds
/// what the model reliably holds in attention. E1 Part 7 Step 6 reverts <see cref="BuildSystem"/>
/// to be metadata-free again — belief extraction is a single, isolated, always-identical-shape
/// prompt regardless of how many metadata fields a document type declares — and adds
/// <see cref="BuildMetadataGroupSystem"/>, a separate builder for ONE metadata field GROUP
/// (~4-5 thematically-related fields) at a time. <see cref="DocumentBeliefExtractor"/> now makes
/// one belief call plus one call per declared metadata group, unioning the metadata results.
/// Proven: a 5-field group hit 100% recall; the same 18 fields asked as one block hit 0%.
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
    /// <summary>Max tokens for the belief extraction response. Five short facts plus reasoning.</summary>
    public const int MaxTokens = 800;

    /// <summary>
    /// Max tokens for a single metadata-group extraction response (E1 Part 7 Step 6) — a group is
    /// ~4-5 fields, each needing a quoted evidence span alongside its value.
    /// </summary>
    public const int MetadataGroupMaxTokens = 1200;

    /// <summary>Max characters of document text included in the belief-pass user prompt.</summary>
    public const int MaxDocChars = 15_000;

    /// <summary>
    /// Max characters of document text for a metadata-group pass (E1 Part 7 Step 6). Real MSAs
    /// run tens of thousands of characters (IIVS's real MSA is ~41,000) with clauses like
    /// governing_law/dispute_resolution/insurance_requirements near the END of the document —
    /// MaxDocChars alone silently truncated the model's view before it ever reached them. The
    /// belief pass keeps MaxDocChars unchanged (same cache keys for every document type,
    /// regardless of metadata depth) — only metadata-group passes use this larger budget.
    /// </summary>
    public const int MaxDocCharsWithMetadata = 60_000;

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
    /// Builds the belief extraction system prompt by concatenating fixed boilerplate with each
    /// key's catalogue <c>prompt_fragment</c> (the original wording, verbatim, for pre-E1 keys).
    /// Deterministic for a given catalogue and key list — same inputs always yield the same
    /// string, byte-for-byte identical to the original hand-authored constant when called with
    /// <see cref="TargetCriteriaOrder"/>. Never touches metadata — this prompt is identical
    /// regardless of a document type's metadata depth (E1 Part 7 Step 6 isolation).
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

    /// <summary>
    /// Builds a metadata-ONLY system prompt for ONE field group (E1 Part 7 Step 6) — no "facts"
    /// array, no belief-key awareness, nothing for the model to juggle except this group's ~4-5
    /// fields. Deliberately simpler than the old combined prompt: with only one array in the
    /// response shape, there is no facts-vs-metadata routing confusion to guard against.
    /// </summary>
    public static string BuildMetadataGroupSystem(
        IReadOnlyDictionary<string, MetadataFieldDefinition> metadataCatalogue,
        IReadOnlyList<string> groupFields)
    {
        var sb = new StringBuilder();
        void Line(string s = "") => sb.Append(s).Append('\n');

        Line("You are a strict commercial-intelligence contract analyst. Extract ONLY the metadata");
        Line("fields listed below, and ONLY when explicitly and unambiguously stated in the document");
        Line("text. Do NOT infer, estimate, approximate, or use outside knowledge.");
        Line();
        Line("METADATA FIELDS (structured facts retained for reference — NOT scored):");

        foreach (var field in groupFields)
        {
            if (!metadataCatalogue.TryGetValue(field, out var def) || string.IsNullOrEmpty(def.PromptFragment))
                throw new InvalidOperationException(
                    $"BeliefExtractionPrompt.BuildMetadataGroupSystem: target metadata field '{field}' " +
                    "has no prompt_fragment in the metadata field catalogue.");

            Line(def.PromptFragment);
        }

        Line();
        Line("ABSTENTION IS MANDATORY: if a field is not explicitly present in the document, DO NOT");
        Line("include it in the output — omit it entirely. Never invent, guess, or infer a value that");
        Line("is not stated. If NONE of these fields appear anywhere in the document, return an empty");
        Line("\"metadata\" array. An empty array is a correct, expected answer — it is not a failure.");
        Line();
        Line("Every field you DO include must carry the exact quoted span of text it was drawn from,");
        Line("so the claim can be checked against the source.");
        Line();
        Line("Return JSON with this exact shape — no markdown fences, just the JSON object:");
        Line("{");
        Line("  \"metadata\": [");
        Line("    {");
        Line($"      \"field\": \"<{string.Join("|", groupFields)}>\",");
        Line("      \"value\": \"<concise plain-text summary of the term, not necessarily verbatim>\",");
        Line("      \"evidence\": \"<exact quoted span from the document text>\"");
        Line("    }");
        Line("  ],");
        Line("  \"confidence\": <float 0.0-1.0>,");
        Line("  \"reasoning\": \"<one sentence>\"");
        sb.Append('}');

        return sb.ToString();
    }

    public static string User(string documentText, int maxChars = MaxDocChars)
    {
        var text = documentText.Length > maxChars
            ? documentText[..maxChars] + "\n[... truncated ...]"
            : documentText;
        return $"Document text:\n\n{text}";
    }
}
