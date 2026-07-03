using System.Text.Json;
using Ii.Contracts;
using Kozmo.Contracts;
using Kozmo.Contracts.Config;
using Kozmo.Llm;

namespace Ii.Observation;

/// <summary>
/// Signal classifier + entity/alias resolver.
/// Rule-based first; falls through to LLM for free-text (payload key "body") when an
/// <see cref="IKozmoLlm"/> is provided. Pass null for rule-only mode (demo default before seed-prep).
/// </summary>
public sealed class ObservationModule : IObservationModule
{
    private const string SystemPrompt =
        """
        You are a vendor health analyst. A customer success manager has submitted a free-text note about a vendor.
        Extract the most important health signal from the note.
        Return ONLY a JSON object with exactly these fields:
        {
          "dimension": "Operational|Experiential|Financial|Strategic",
          "criterion": "one of: uptime_sla, csat_score, adoption_rate, payment_timeliness, renewal_intent, roadmap_fit_score",
          "value": <number 0.0 to 1.0 where 0.0 = worst performance, 1.0 = best>,
          "confidence": <number 0.0 to 1.0 reflecting your certainty>,
          "reasoning": "<one sentence explanation>"
        }
        """;

    private readonly IKozmoLlm? _llm;

    public ObservationModule(IKozmoLlm? llm = null) => _llm = llm;

    public ClassificationResult? Classify(Signal signal, SaasProfile profile)
    {
        // 1. Rule-based path — fast, deterministic
        foreach (var rule in profile.ClassificationRules)
        {
            if (!rule.SourceSystem.Equals(signal.SourceSystem.ToString(), StringComparison.OrdinalIgnoreCase))
                continue;

            if (!signal.Payload.TryGetValue(rule.PayloadKey, out var rawValue) || rawValue == null)
                continue;

            var score = ScoreFromRubric(rule.Criterion, rawValue, profile);
            if (score == null) continue;

            if (!Enum.TryParse<Dimension>(rule.Dimension, ignoreCase: true, out var dim)) continue;
            if (!Enum.TryParse<SourceTier>(rule.Tier,      ignoreCase: true, out var tier)) continue;

            return new ClassificationResult(
                Dimension:  dim,
                Criterion:  rule.Criterion,
                Value:      score.Value,
                SourceTier: tier,
                Derivation: $"rule:{rule.SourceSystem}/{rule.PayloadKey}");
        }

        // 2. LLM residue — any unclassified signal falls to LLM when configured
        if (_llm != null)
        {
            var text = ExtractText(signal.Payload);
            if (!string.IsNullOrWhiteSpace(text))
                return ClassifyWithLlm(signal, text);
        }

        return null;
    }

    public Guid ResolveEntity(string entityRef, Guid fallbackEntityId, SaasProfile profile)
    {
        if (string.IsNullOrWhiteSpace(entityRef)) return fallbackEntityId;

        // Exact alias map lookup first
        var er = profile.EntityResolution;
        if (er.AliasMap.TryGetValue(entityRef, out var canonical))
        {
            // canonical is a string entity reference; the caller maps to GUID via their entity registry
            // For Phase 0 the alias map returns a canonical string — spine handles GUID lookup
        }

        // Fuzzy match not implemented in Phase 0; fallback to the signal's own entity ID
        return fallbackEntityId;
    }

    // ── Text extraction ───────────────────────────────────────────────────────

    /// <summary>
    /// Prefer "body" key if present; otherwise serialize the payload as JSON text.
    /// </summary>
    private static string ExtractText(IReadOnlyDictionary<string, object?> payload)
    {
        if (payload.TryGetValue("body", out var bodyObj) && bodyObj != null)
        {
            var body = bodyObj.ToString() ?? "";
            if (!string.IsNullOrWhiteSpace(body)) return body;
        }
        return JsonSerializer.Serialize(payload);
    }

    // ── LLM path ─────────────────────────────────────────────────────────────

    private ClassificationResult? ClassifyWithLlm(Signal signal, string body)
    {
        LlmResult result = _llm!.CompleteJsonAsync(SystemPrompt, body, maxTokens: 200)
                                 .GetAwaiter().GetResult();

        return ParseLlmResult(result, signal.SourceSystem);
    }

    private static ClassificationResult? ParseLlmResult(LlmResult result, SourceSystem sourceSystem)
    {
        if (result.Answer is not JsonElement je) return null;

        if (!je.TryGetProperty("dimension", out var dimEl) ||
            !je.TryGetProperty("criterion", out var critEl) ||
            !je.TryGetProperty("value",     out var valEl))
            return null;

        var dimStr = dimEl.GetString() ?? "";
        var crit   = critEl.GetString() ?? "";
        if (!Enum.TryParse<Dimension>(dimStr, ignoreCase: true, out var dim)) return null;
        if (string.IsNullOrEmpty(crit)) return null;

        var value      = valEl.GetDouble();
        var confidence = je.TryGetProperty("confidence", out var confEl) ? confEl.GetDouble() : 0.7;
        var reasoning  = je.TryGetProperty("reasoning",  out var rsEl)   ? rsEl.GetString() ?? "" : "";

        // Tier is always Reported for HumanReport free-text; Inferred otherwise
        var tier = sourceSystem == SourceSystem.HumanReport ? SourceTier.Reported : SourceTier.Inferred;

        return new ClassificationResult(
            Dimension:  dim,
            Criterion:  crit,
            Value:      value,
            SourceTier: tier,
            Derivation: $"llm:{sourceSystem}")
        {
            Method           = ClassificationMethod.Llm,
            MethodConfidence = confidence,
            ReasoningSummary = reasoning
        };
    }

    // ── Rule-based rubric scoring ─────────────────────────────────────────────

    /// <summary>
    /// Bands a raw magnitude into a 0-1 rubric score via the named scoring_rubric.saas.v1.json
    /// criterion (numeric thresholds or enum scores). Public so other belief-writing paths
    /// (e.g. Kyv.ProgramRunner's belief-persistence stage) can reuse this proven banding logic
    /// instead of reimplementing it — see KYV_KNOWN_GAPS.md "Belief bridge Commit 1 -> Commit 2".
    /// Returns null if the criterion is unknown or the value is out of the criterion's domain.
    /// </summary>
    public static double? ScoreFromRubric(string criterion, object rawValue, SaasProfile profile)
    {
        if (!profile.ScoringRubric.TryGetValue(criterion, out var rubric)) return null;

        if (rubric.Type == "enum")
        {
            var key = rawValue.ToString() ?? "";
            return rubric.EnumScores != null && rubric.EnumScores.TryGetValue(key, out var es) ? es : null;
        }

        if (rubric.Type == "numeric" && rubric.NumericThresholds != null)
        {
            var num = ToDouble(rawValue);
            if (num == null) return null;
            return ApplyNumericThresholds(num.Value, rubric.NumericThresholds);
        }

        return null;
    }

    private static double? ApplyNumericThresholds(double value, IReadOnlyList<RubricThreshold> thresholds)
    {
        // Domain is [min(Min), max(Max)] across all buckets, regardless of the list's own order
        // (some criteria are authored ascending, others descending). A value outside that domain
        // is out-of-range for this rubric and must not silently fall into whichever bucket
        // happens to be last in the array — abstain (return null) instead of guessing.
        var domainMin = thresholds.Min(t => t.Min);
        var domainMax = thresholds.Max(t => t.Max);
        if (value < domainMin || value > domainMax) return null;

        foreach (var t in thresholds)
        {
            // Upper bound is exclusive except for the bucket that owns the domain ceiling —
            // otherwise a value exactly at the top of the range (e.g. 100% uptime) would match
            // no bucket at all.
            var isTopBucket = t.Max == domainMax;
            var inRange     = value >= t.Min && (value < t.Max || (isTopBucket && value <= t.Max));
            if (inRange) return t.Score;
        }
        return null;
    }

    private static double? ToDouble(object v) => v switch
    {
        double d   => d,
        float  f   => (double)f,
        int    i   => (double)i,
        long   l   => (double)l,
        decimal dec => (double)dec,
        string s   => double.TryParse(s, System.Globalization.NumberStyles.Any,
                                      System.Globalization.CultureInfo.InvariantCulture, out var r) ? r : null,
        JsonElement je when je.ValueKind == JsonValueKind.Number
                   => je.GetDouble(),
        _          => null
    };
}
