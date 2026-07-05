using System.Text;
using Kozmo.Contracts.Config;

namespace Ii.CandidateExtraction;

/// <summary>
/// System prompt builder for the email belief-extraction LLM call (E-signal Part 5 Step 4).
/// Catalogue-driven exactly like <see cref="BeliefExtractionPrompt.BuildSystem"/> — same claim-key
/// catalogue, same fragment-projection mechanic — but a deliberately SEPARATE class, not a change to
/// <see cref="BeliefExtractionPrompt"/>: email needs a stricter framing than a document (an email is
/// informal correspondence, not a contract) and this must never risk perturbing
/// <c>BeliefExtractionPromptGenerationTests</c>' byte-stable pin on the document path (E1's
/// composition-ripple lesson — a change made for one input type rippling into another's cassette
/// keys).
/// <para>
/// This step defines and proves the prompt GENERATES ONLY — no LLM call, no parsing, no wiring into
/// <c>DocumentBeliefExtractor</c>/<c>KyvProgramRunner</c>. That is E-signal Part 5 Step 5.
/// </para>
/// <para>
/// The signal side (relationship intelligence: sentiment, commitment, issue_raised,
/// stakeholder_signal, request) reuses the SAME storage mechanism as document metadata
/// (E-signal Part 5 §2.4 Decision 2 routes signals through <c>Km.Store.Metadata</c> — a signal
/// type is structurally just another metadata field, same catalogue file, same
/// definition/positive_example/negative_example/prompt_fragment shape as MSA's 18 clause fields).
/// <see cref="BuildSignalSystem"/> mirrors <see cref="BeliefExtractionPrompt.BuildMetadataGroupSystem"/>'s
/// mechanics exactly (same JSON response shape, so Step 5 can reuse the same parsing logic) but
/// with EMAIL-accurate framing — <c>BuildMetadataGroupSystem</c>'s wording ("contract analyst",
/// "document text") is correct for MSA clauses and wrong for correspondence; a separate builder
/// avoids stretching one prompt's wording to cover two very different input shapes, the same
/// isolation reasoning as <see cref="BuildBeliefSystem"/> above. It is NOT a change to
/// <c>BeliefExtractionPrompt</c> — MSA's cassette-pinned metadata-group prompts are untouched.
/// </para>
/// </summary>
public static class EmailInterpretationPrompt
{
    /// <summary>Max tokens for the email belief-extraction response — a handful of short facts.</summary>
    public const int BeliefMaxTokens = 600;

    /// <summary>Max tokens for the email signal-extraction response — up to 5 short signals.</summary>
    public const int SignalMaxTokens = 800;

    /// <summary>Max characters of email body included in the belief-pass user prompt. Emails are short.</summary>
    public const int MaxBodyChars = 8_000;

    /// <summary>
    /// Builds the email belief-extraction system prompt: fixed boilerplate plus each target key's
    /// catalogue <c>prompt_fragment</c> (the SAME fragments <see cref="BeliefExtractionPrompt"/>
    /// projects for documents — email is just another, weaker source for the same catalogue keys,
    /// never new keys). The boilerplate is deliberately stricter than the document prompt's: this
    /// is the belief-vs-signal routing discipline (Kozmo_Phase_E_Signal_Spec.md §2.3/§7, the crux
    /// of this phase) — casual mentions, hypotheticals, questions, and negotiations-in-progress
    /// must never become beliefs, only clean, explicit, settled statements of fact may. Deterministic
    /// for a given catalogue and key list — same inputs always yield the same string.
    /// </summary>
    public static string BuildBeliefSystem(
        IReadOnlyDictionary<string, ClaimKeyDefinition> catalogue,
        IReadOnlyList<string> targetCriteriaOrder)
    {
        var sb = new StringBuilder();
        void Line(string s = "") => sb.Append(s).Append('\n');

        Line("You are a strict commercial-intelligence fact extractor reading an EMAIL — informal");
        Line("correspondence, not a signed contract. Extract ONLY the facts listed below, and ONLY");
        Line("when EXPLICITLY and UNAMBIGUOUSLY stated as a settled fact in the email text. Do NOT");
        Line("infer, estimate, approximate, or use outside knowledge.");
        Line();
        Line("THE HARD RULE FOR EMAIL (stricter than for a contract, because correspondence is more");
        Line("informal and imprecise): a casual mention, a hypothetical, a question, or a term still");
        Line("being negotiated is NEVER a fact — even if it names one of the facts below. Only a");
        Line("clean, explicit, settled statement of the fact qualifies. If the email merely raises,");
        Line("discusses, proposes, asks about, or negotiates one of these facts without a clear,");
        Line("explicit, agreed statement, DO NOT extract it — that is relationship context, handled");
        Line("elsewhere, not this extractor's job. When in doubt, leave it out.");
        Line();
        Line("A HEDGED NUMBER IS NEVER A FACT: watch for words like \"roughly\", \"approximately\",");
        Line("\"estimated\", \"ballpark\", \"in the range of\", \"starting point\", or \"as we finalize\" —");
        Line("these mark a PROPOSAL still being negotiated, not an agreed figure, even when a specific");
        Line("dollar amount is stated right next to them (e.g. \"roughly $14.50/seat, approximately");
        Line("$147,900 annually — this is a starting point\" is a pricing proposal, NOT a settled");
        Line("annual_value or invoice_amount — omit it; that is a commitment/discussion signal,");
        Line("handled elsewhere, not this extractor's job).");
        Line();
        Line("FACTS (criterion key -> what to look for -> raw value encoding):");

        foreach (var key in targetCriteriaOrder)
        {
            if (!catalogue.TryGetValue(key, out var def) || string.IsNullOrEmpty(def.PromptFragment))
                throw new InvalidOperationException(
                    $"EmailInterpretationPrompt.BuildBeliefSystem: target criterion '{key}' has no " +
                    "prompt_fragment in the claim key catalogue.");

            Line(def.PromptFragment);
        }

        Line();
        Line("ABSTENTION IS MANDATORY — EVEN MORE SO THAN FOR A CONTRACT: if a fact is not stated as");
        Line("a clear, explicit, settled fact, DO NOT include it in the output — omit it entirely.");
        Line("Never invent, guess, or infer a value that is not stated. If NONE of the facts appear");
        Line("as explicit statements, return an empty \"facts\" array. An empty array is the correct,");
        Line("expected answer for most emails (a status update, a scheduling note, a thank-you) — it");
        Line("is not a failure.");
        Line();
        Line("Every fact you DO include must carry the exact quoted span of text it was drawn from,");
        Line("so the claim can be checked against the source.");
        Line();
        Line("Return JSON with this exact shape — no markdown fences, just the JSON object:");
        Line("{");
        Line("  \"facts\": [");
        Line("    {");
        Line($"      \"criterion\": \"<{string.Join("|", targetCriteriaOrder)}>\",");
        Line("      \"value\": <number, EXCEPT renewal_date which is the string \"YYYY-MM-DD\">,");
        Line("      \"evidence\": \"<exact quoted span from the email text>\",");
        Line("      \"confidence\": <float 0.0-1.0>");
        Line("    }");
        Line("  ],");
        Line("  \"confidence\": <float 0.0-1.0>,");
        Line("  \"reasoning\": \"<one sentence>\"");
        sb.Append('}');

        return sb.ToString();
    }

    /// <summary>
    /// Builds the email signal-extraction system prompt for ONE signal-type group — mirrors
    /// <see cref="BeliefExtractionPrompt.BuildMetadataGroupSystem"/>'s mechanics (same catalogue
    /// projection, same JSON response shape: <c>{"metadata": [{"field","value","evidence"}], ...}</c>,
    /// so Step 5's parsing can reuse the same logic that already parses document metadata groups)
    /// but framed correctly for correspondence rather than a contract. Deterministic for a given
    /// catalogue and field list. "responsiveness" must never appear in <paramref name="groupFields"/>
    /// — it is computed deterministically from timestamps, never LLM-interpreted (spec Appendix #2).
    /// </summary>
    public static string BuildSignalSystem(
        IReadOnlyDictionary<string, MetadataFieldDefinition> signalCatalogue,
        IReadOnlyList<string> groupFields)
    {
        var sb = new StringBuilder();
        void Line(string s = "") => sb.Append(s).Append('\n');

        Line("You are reading an EMAIL for relationship intelligence — not for contractual facts.");
        Line("Identify ONLY the relationship signals listed below, and ONLY when clearly expressed");
        Line("in the email text. Do NOT infer, estimate, approximate, or use outside knowledge.");
        Line();
        Line("SIGNALS (relationship intelligence — NOT scored, retained for the relationship record):");

        foreach (var field in groupFields)
        {
            if (!signalCatalogue.TryGetValue(field, out var def) || string.IsNullOrEmpty(def.PromptFragment))
                throw new InvalidOperationException(
                    $"EmailInterpretationPrompt.BuildSignalSystem: target signal type '{field}' " +
                    "has no prompt_fragment in the metadata field catalogue.");

            Line(def.PromptFragment);
        }

        Line();
        Line("ABSTENTION IS MANDATORY: if a signal is not clearly expressed, DO NOT include it in");
        Line("the output — omit it entirely. Never invent, guess, or infer a signal that isn't");
        Line("there. If NONE of these signals appear, return an empty \"metadata\" array. An empty");
        Line("array is a correct, expected answer for a purely transactional email (e.g. a plain");
        Line("invoice or a scheduling note) — it is not a failure.");
        Line();
        Line("Every signal you DO include must carry the exact quoted span of text it was drawn");
        Line("from, so the claim can be checked against the source.");
        Line();
        Line("Return JSON with this exact shape — no markdown fences, just the JSON object:");
        Line("{");
        Line("  \"metadata\": [");
        Line("    {");
        Line($"      \"field\": \"<{string.Join("|", groupFields)}>\",");
        Line("      \"value\": \"<concise plain-text summary, not necessarily verbatim>\",");
        Line("      \"evidence\": \"<exact quoted span from the email text>\"");
        Line("    }");
        Line("  ],");
        Line("  \"confidence\": <float 0.0-1.0>,");
        Line("  \"reasoning\": \"<one sentence>\"");
        sb.Append('}');

        return sb.ToString();
    }

    /// <summary>Wraps the email body for the belief-pass user prompt.</summary>
    public static string User(string emailBody, int maxChars = MaxBodyChars)
    {
        var text = emailBody.Length > maxChars
            ? emailBody[..maxChars] + "\n[... truncated ...]"
            : emailBody;
        return $"Email body:\n\n{text}";
    }
}
