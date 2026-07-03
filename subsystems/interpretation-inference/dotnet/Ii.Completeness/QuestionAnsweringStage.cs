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

        foreach (var q in ordered)
        {
            var user   = AnsweringPrompt.User(q, beliefs, _profile);
            var result = await _llm.CompleteJsonAsync(
                AnsweringPrompt.System, user, AnsweringPrompt.MaxTokens, ct);

            answers.Add(ParseAnswer(result, q.Id, vendorId, now));
        }

        return answers.AsReadOnly();
    }

    // Parses { answer, confidence, cited_belief_ids, reasoning } from the LLM result.
    // Any parse failure → UNKNOWN / 0.10 so a malformed response is treated as a gap,
    // never as a crash or a fabricated answer.
    private static Answer ParseAnswer(
        LlmResult      result,
        string         questionId,
        Guid           vendorId,
        DateTimeOffset now)
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

        var citedIds = ParseCitedIds(root);

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

    private static IReadOnlyList<Guid> ParseCitedIds(JsonElement root)
    {
        if (!root.TryGetProperty("cited_belief_ids", out var arr) ||
            arr.ValueKind != JsonValueKind.Array)
            return [];

        var ids = new List<Guid>();
        foreach (var el in arr.EnumerateArray())
        {
            var s = el.ValueKind == JsonValueKind.String ? el.GetString() : null;
            if (s != null && Guid.TryParse(s, out var id))
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
