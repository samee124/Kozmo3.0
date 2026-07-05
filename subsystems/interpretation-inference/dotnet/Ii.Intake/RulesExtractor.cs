using System.Text.Json;
using Kozmo.Contracts;
using Kozmo.Contracts.Config;

namespace Ii.Intake;

/// <summary>
/// Rules-based intake lane for CSV / semi-structured documents (§6 of spec — Lane: Rules).
/// Reads a pre-processed evidence fixture (JSON) and emits VERIFIED/PRIMARY beliefs.
/// Provenance locators: "row:N", "cell:R,C", "field:name".
/// </summary>
public sealed class RulesExtractor
{
    private readonly SaasProfile _profile;

    public RulesExtractor(SaasProfile profile)
    {
        _profile = profile;
    }

    /// <summary>
    /// Extract beliefs from a pre-processed evidence fixture file.
    /// The fixture JSON contains a list of claim extractions with locators and raw values.
    /// </summary>
    public IReadOnlyList<ExtractedClaim> Extract(
        Evidence        evidence,
        string          fixtureJson,
        DateTimeOffset  observedAt)
    {
        var doc = JsonDocument.Parse(fixtureJson);
        var results = new List<ExtractedClaim>();

        if (!doc.RootElement.TryGetProperty("claims", out var claims)) return results;

        foreach (var item in claims.EnumerateArray())
        {
            var claimKey = item.GetProperty("claim_key").GetString() ?? "";
            if (string.IsNullOrEmpty(claimKey)) continue;

            if (!_profile.ClaimKeyCatalogue.TryGetValue(claimKey, out var ckDef)) continue;

            var rawValue         = item.GetProperty("raw_value").GetDouble();
            var normValue        = NormaliseValue(claimKey, rawValue, ckDef);
            var extractorConf    = item.TryGetProperty("extractor_confidence", out var ec)
                                       ? ec.GetDouble() : 1.0;
            var locator          = item.TryGetProperty("locator", out var loc)
                                       ? loc.GetString() ?? "field:unknown" : "field:unknown";

            if (!Enum.TryParse<Dimension>(ckDef.Dimension, ignoreCase: true, out var dimension)
                && ckDef.ClaimClass == "scored") continue;

            if (!Enum.TryParse<SourceTier>(evidence.SourceTier.ToString(), out var tier))
                tier = evidence.SourceTier;

            results.Add(new ExtractedClaim(
                ClaimKey:          claimKey,
                Dimension:         dimension,
                Criterion:         claimKey,
                NormalisedValue:   normValue,
                Tier:              tier,
                ExtractorConfidence: extractorConf,
                ObservedAt:        observedAt,
                Locator:           locator,
                EvidenceId:        evidence.EvidenceId));
        }

        return results;
    }

    private double NormaliseValue(string claimKey, double rawValue, ClaimKeyDefinition ckDef)
    {
        // For scored claims, apply the scoring rubric if present; else treat raw as 0–1 already.
        // A claim key's rubric criterion name can differ from the claim key itself (e.g.
        // sla_uptime -> uptime_sla) — ClaimKeyDefinition.RubricCriterion is the single catalogue-
        // driven source of that translation (E1 Part 7 Step 7 Fix 4).
        var rubricCriterion = ckDef.RubricCriterion ?? claimKey;
        if (ckDef.ClaimClass == "scored" && _profile.ScoringRubric.TryGetValue(rubricCriterion, out var rubric))
        {
            if (rubric.Type == "numeric" && rubric.NumericThresholds != null)
            {
                foreach (var t in rubric.NumericThresholds)
                    if (rawValue >= t.Min && rawValue < t.Max) return t.Score;
                // Fallback: last threshold max
                var last = rubric.NumericThresholds[^1];
                return rawValue >= last.Max ? last.Score : rubric.NumericThresholds[0].Score;
            }
            if (rubric.Type == "enum" && rubric.EnumScores != null)
            {
                var key = rawValue.ToString("G");
                return rubric.EnumScores.TryGetValue(key, out var s) ? s : rawValue;
            }
        }
        return ckDef.ClaimClass == "structural" ? rawValue : Math.Clamp(rawValue, 0.0, 1.0);
    }
}

public sealed record ExtractedClaim(
    string         ClaimKey,
    Dimension      Dimension,
    string         Criterion,
    double         NormalisedValue,
    SourceTier     Tier,
    double         ExtractorConfidence,
    DateTimeOffset ObservedAt,
    string         Locator,
    Guid           EvidenceId
);
