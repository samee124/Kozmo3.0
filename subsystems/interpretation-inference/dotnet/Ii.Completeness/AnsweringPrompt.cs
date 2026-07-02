using System.Text.Json;
using System.Text.Json.Serialization;
using Kozmo.Contracts;

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

    public static string User(Question question, IReadOnlyList<Belief> beliefs)
    {
        var beliefJson = SerializeBeliefs(beliefs);
        if (beliefJson.Length > MaxBeliefChars)
            beliefJson = beliefJson[..MaxBeliefChars] + "\n  ... [truncated to fit context window]";

        return
            $"Question (id: {question.Id}, type: {question.AnswerType}):\n" +
            $"{question.Text}\n\n" +
            $"Vendor beliefs ({beliefs.Count} item(s)):\n" +
            beliefJson;
    }

    // Stable, minimal JSON — only the fields the LLM needs to reason and cite.
    // Sorted by Id for determinism; uses string enums for readability.
    internal static string SerializeBeliefs(IReadOnlyList<Belief> beliefs)
    {
        var items = beliefs
            .OrderBy(b => b.Id)
            .Select(b => new BeliefView(
                b.Id.ToString(),
                b.Dimension?.ToString() ?? "Unknown",
                b.Criterion,
                b.SourceTier.ToString(),
                b.Confidence,
                b.Derivation));

        return JsonSerializer.Serialize(items, PromptJsonOpts);
    }

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
