using Ii.Contracts;
using Kozmo.Contracts;
using Kozmo.Contracts.Config;

namespace Ii.Observation;

/// <summary>
/// Rule-based signal classifier + entity/alias resolver.
/// No LLM, no network — rules only.
/// </summary>
public sealed class ObservationModule : IObservationModule
{
    public ClassificationResult? Classify(Signal signal, SaasProfile profile)
    {
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

    // ── rubric scoring ────────────────────────────────────────────────────────

    private static double? ScoreFromRubric(string criterion, object rawValue, SaasProfile profile)
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
        // Find the last threshold where value >= min (thresholds ordered high-to-low min, or use first where value < max)
        for (var i = 0; i < thresholds.Count; i++)
        {
            var t = thresholds[i];
            bool inRange = value >= t.Min && (i == thresholds.Count - 1 || value < t.Max);
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
        System.Text.Json.JsonElement je when je.ValueKind == System.Text.Json.JsonValueKind.Number
                   => je.GetDouble(),
        _          => null
    };
}
