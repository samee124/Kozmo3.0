using System.Text.Json;
using Kozmo.Contracts;
using Kozmo.Contracts.Config;
using Kozmo.Llm;

namespace Ii.Completeness;

/// <summary>
/// Soft-edge answering stage: for each selected question, calls the LLM via
/// <see cref="IKozmoLlm.CompleteJsonAsync"/> with the question + vendor beliefs as evidence.
/// In demo/test mode the LLM is a <see cref="CachingLlmClient"/> in replay mode — no live
/// network call ever leaves the process. In record mode the recorder tool wraps it with the
/// real OpenAI client (Kozmo.Llm.OpenAi, banned from this assembly by BannedSymbols.txt).
///
/// Grounding discipline: answers cite the belief IDs they used. Questions with no supporting
/// belief → UNKNOWN / confidence ≤ 0.30 → the rubric counts them as gaps. The LLM does not
/// invent; the absence of evidence IS a first-class result.
///
/// Caller supplies <paramref name="now"/> — this class never reads the clock (Invariant #3).
/// </summary>
public sealed class QuestionAnsweringStage
{
    private readonly IKozmoLlm    _llm;
    private readonly SaasProfile? _profile;

    /// <param name="profile">
    /// Optional — supplies source-tier ceilings so AnsweringPrompt can derive a presentation-only
    /// evidence weight for structural beliefs (Confidence == 0). Null preserves prior behavior
    /// (the belief's raw persisted Confidence is shown as-is); every existing caller that doesn't
    /// pass real structural beliefs is unaffected either way.
    /// </param>
    public QuestionAnsweringStage(IKozmoLlm llm, SaasProfile? profile = null)
    {
        _llm     = llm;
        _profile = profile;
    }

    /// <summary>
    /// Answer every question in <paramref name="questions"/> from the vendor's belief evidence.
    /// Questions are processed in stable order (sorted by Question.Id) so the cassette key is
    /// independent of the order in which the caller built the question list.
    /// </summary>
    public async Task<IReadOnlyList<Answer>> AnswerAsync(
        Guid                    vendorId,
        IReadOnlyList<Question> questions,
        IReadOnlyList<Belief>   beliefs,
        DateTimeOffset          now,
        CancellationToken       ct = default)
    {
        var ordered = questions.OrderBy(q => q.Id, StringComparer.Ordinal).ToList();
        var answers = new List<Answer>(ordered.Count);

        // Same ordering AnsweringPrompt.SerializeBeliefs uses internally — the ordinal labels
        // shown to the LLM and this map must agree, or citations resolve to the wrong belief.
        var ordinalToId = AnsweringPrompt.OrderForPrompt(beliefs)
            .Select((b, i) => (Ordinal: i + 1, b.Id))
            .ToDictionary(x => x.Ordinal, x => x.Id);

        foreach (var q in ordered)
        {
            var user   = AnsweringPrompt.User(q, beliefs, _profile);
            var result = await _llm.CompleteJsonAsync(
                AnsweringPrompt.System, user, AnsweringPrompt.MaxTokens, ct);

            answers.Add(ParseAnswer(result, q.Id, vendorId, now, ordinalToId));
        }

        return answers.AsReadOnly();
    }

    // Parses { answer, confidence, cited_belief_ids, reasoning } from the LLM result.
    // Any parse failure → UNKNOWN / 0.10 so a malformed response is treated as a gap,
    // never as a crash or a fabricated answer.
    private static Answer ParseAnswer(
        LlmResult                     result,
        string                        questionId,
        Guid                          vendorId,
        DateTimeOffset                now,
        IReadOnlyDictionary<int, Guid> ordinalToId)
    {
        const double UnknownConfidence = 0.10;

        if (result.Answer is not JsonElement root || root.ValueKind != JsonValueKind.Object)
            return Unknown(questionId, vendorId, now, UnknownConfidence, "LLM returned no JSON object");

        var value = GetString(root, "answer") ?? "UNKNOWN";
        var confidence = GetDouble(root, "confidence") ?? UnknownConfidence;

        // Enforce UNKNOWN discipline: if the model says UNKNOWN but confidence is above the
        // low bar, clamp it — the answer shape and confidence must be consistent.
        if (value.Equals("UNKNOWN", StringComparison.OrdinalIgnoreCase))
            confidence = Math.Min(confidence, 0.30);

        var citedIds = ParseCitedIds(root, ordinalToId);

        return new Answer(questionId, value, confidence)
        {
            VendorId       = vendorId,
            CitedBeliefIds = citedIds,
            AnsweredAt     = now,
        };
    }

    private static Answer Unknown(
        string questionId, Guid vendorId, DateTimeOffset now,
        double confidence, string reason) =>
        new(questionId, "UNKNOWN", confidence)
        {
            VendorId       = vendorId,
            CitedBeliefIds = [],
            AnsweredAt     = now,
        };

    // Cited ids are small 1-based ordinals now (see AnsweringPrompt.SerializeBeliefs), not real
    // belief Guids — the model may return them as a JSON string ("2") or a bare number (2);
    // both are accepted. An ordinal outside the range that was actually offered in the prompt
    // is silently dropped, same discipline as the old GUID-parse failure path: a malformed or
    // hallucinated citation degrades the citation list, never the run.
    private static IReadOnlyList<Guid> ParseCitedIds(JsonElement root, IReadOnlyDictionary<int, Guid> ordinalToId)
    {
        if (!root.TryGetProperty("cited_belief_ids", out var arr) ||
            arr.ValueKind != JsonValueKind.Array)
            return [];

        var ids = new List<Guid>();
        foreach (var el in arr.EnumerateArray())
        {
            int? ordinal = el.ValueKind switch
            {
                JsonValueKind.Number when el.TryGetInt32(out var n)              => n,
                JsonValueKind.String when int.TryParse(el.GetString(), out var n) => n,
                _                                                                 => null,
            };

            if (ordinal.HasValue && ordinalToId.TryGetValue(ordinal.Value, out var id))
                ids.Add(id);
        }
        return ids.AsReadOnly();
    }

    // Accepts string, number (TypedValue questions — model may omit quotes), and bool scalars.
    // GetRawText() on a Number gives the bare digits, e.g. 285000 → "285000".
    private static string? GetString(JsonElement el, string prop)
    {
        if (!el.TryGetProperty(prop, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            JsonValueKind.True   => "true",
            JsonValueKind.False  => "false",
            _                    => null,
        };
    }

    private static double? GetDouble(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetDouble()
            : null;
}
