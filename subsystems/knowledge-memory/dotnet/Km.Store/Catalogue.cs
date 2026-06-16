using System.Text.Json;
using System.Text.Json.Nodes;
using Kozmo.Contracts.Config;
using Kozmo.Contracts.Interfaces;

namespace Km.Store;

/// <summary>
/// Loads the nine *.saas.v1.json catalogue configs from a directory.
/// Validates on load — bad config is rejected with a clear error, never silently normalised.
/// </summary>
public sealed class Catalogue : ICatalogue
{
    public SaasProfile Load(string profileDirectory)
    {
        var dir = profileDirectory;
        var dims     = LoadJson(dir, "dimensions.saas.v1.json");
        var rubric   = LoadJson(dir, "scoring_rubric.saas.v1.json");
        var weights  = LoadJson(dir, "dimension_weights.saas.v1.json");
        var bands    = LoadJson(dir, "bands.saas.v1.json");
        var postures = LoadJson(dir, "postures.saas.v1.json");
        var tiers    = LoadJson(dir, "source_tiers.saas.v1.json");
        var classify = LoadJson(dir, "classification.saas.v1.json");
        var decay    = LoadJson(dir, "decay.saas.v1.json");
        var entityRes= LoadJson(dir, "entity_resolution.saas.v1.json");

        var profile = Assemble(dims, rubric, weights, bands, postures, tiers, classify, decay, entityRes);
        Validate(profile);
        return profile;
    }

    private static JsonObject LoadJson(string dir, string file)
    {
        var path = Path.Combine(dir, file);
        if (!File.Exists(path))
            throw new InvalidOperationException($"Catalogue file missing: {path}");
        var text = File.ReadAllText(path);
        return JsonNode.Parse(text)?.AsObject()
            ?? throw new InvalidOperationException($"Catalogue file is not a JSON object: {path}");
    }

    private static SaasProfile Assemble(
        JsonObject dims, JsonObject rubric, JsonObject weights, JsonObject bands,
        JsonObject postures, JsonObject tiers, JsonObject classify, JsonObject decay, JsonObject entityRes)
    {
        var version = weights["version"]?.GetValue<string>() ?? "unknown";

        // Dimensions
        var dimDefs = new Dictionary<string, DimensionDefinition>();
        foreach (var kv in dims["dimensions"]!.AsObject())
        {
            var obj      = kv.Value!.AsObject();
            var desc     = obj["description"]?.GetValue<string>() ?? "";
            var criteria = obj["criteria"]?.AsArray().Select(c => c!.GetValue<string>()).ToList()
                           ?? new List<string>();
            dimDefs[kv.Key] = new DimensionDefinition(desc, criteria);
        }

        // Scoring rubric
        var rubricDefs = new Dictionary<string, CriterionRubric>();
        foreach (var kv in rubric["criteria"]!.AsObject())
        {
            var obj  = kv.Value!.AsObject();
            var type = obj["type"]?.GetValue<string>() ?? "numeric";
            List<RubricThreshold>?                  thresholds = null;
            Dictionary<string, double>?             enumScores = null;

            if (type == "numeric" && obj["thresholds"] is JsonArray ta)
            {
                thresholds = ta.Select(t =>
                {
                    var to = t!.AsObject();
                    return new RubricThreshold(
                        to["min"]!.GetValue<double>(),
                        to["max"]!.GetValue<double>(),
                        to["score"]!.GetValue<double>());
                }).ToList();
            }
            else if (type == "enum" && obj["scores"] is JsonObject so)
            {
                enumScores = so.ToDictionary(e => e.Key, e => e.Value!.GetValue<double>());
            }

            rubricDefs[kv.Key] = new CriterionRubric(type, thresholds, enumScores);
        }

        // Dimension weights
        var weightMap = new Dictionary<string, double>();
        foreach (var kv in weights["weights"]!.AsObject())
            weightMap[kv.Key] = kv.Value!.GetValue<double>();

        // Bands
        var bandsObj    = bands["bands"]!.AsObject();
        var floorObj    = bands["confidence_floor"]!.AsObject();
        var bandsConfig = new BandsConfig(
            HealthyMin:              bandsObj["Healthy"]!.AsObject()["min_composite"]!.GetValue<double>(),
            AtRiskMin:               bandsObj["AtRisk"]!.AsObject()["min_composite"]!.GetValue<double>(),
            CriticalConfidenceGate:  floorObj["critical_min_confidence"]!.GetValue<double>(),
            PerContradictionPenalty: floorObj["per_contradiction_penalty"]!.GetValue<double>(),
            PerGapPenalty:           floorObj["per_gap_penalty"]!.GetValue<double>()
        );

        // Posture rules
        var postureRules = postures["rules"]!.AsArray().Select(r =>
        {
            var ro = r!.AsObject();
            int? renewalDays = ro["renewal_within_days"]?.GetValue<int?>();
            return new PostureRule(
                Band:              ro["band"]!.GetValue<string>(),
                Pattern:           ro["pattern"]!.GetValue<string>(),
                RenewalWithinDays: renewalDays,
                Stance:            ro["stance"]!.GetValue<string>());
        }).ToList();

        // Source tiers
        var tierMap = new Dictionary<string, SourceTierConfig>();
        foreach (var kv in tiers["tiers"]!.AsObject())
        {
            var to = kv.Value!.AsObject();
            tierMap[kv.Key] = new SourceTierConfig(
                to["weight"]!.GetValue<double>(),
                to["description"]?.GetValue<string>() ?? "");
        }

        // Classification rules
        var classifyRules = classify["rules"]!.AsArray().Select(r =>
        {
            var ro = r!.AsObject();
            return new ClassificationRule(
                SourceSystem: ro["source_system"]!.GetValue<string>(),
                PayloadKey:   ro["payload_key"]!.GetValue<string>(),
                Dimension:    ro["dimension"]!.GetValue<string>(),
                Criterion:    ro["criterion"]!.GetValue<string>(),
                Tier:         ro["tier"]!.GetValue<string>());
        }).ToList();

        // Decay half-lives
        var halfLifeMap = new Dictionary<string, int>();
        foreach (var kv in decay["half_life_by_tier"]!.AsObject())
            halfLifeMap[kv.Key] = kv.Value!.GetValue<int>();

        // Entity resolution
        var erObj    = entityRes.AsObject();
        var aliasMap = new Dictionary<string, string>();
        if (erObj["alias_map"] is JsonObject am)
            foreach (var kv in am)
                aliasMap[kv.Key] = kv.Value!.GetValue<string>();

        var erConfig = new EntityResolutionConfig(
            Strategy:       erObj["strategy"]?.GetValue<string>() ?? "fuzzy_canonical",
            FuzzyThreshold: erObj["fuzzy_threshold"]?.GetValue<double>() ?? 0.80,
            AliasMap:       aliasMap);

        return new SaasProfile(
            ConfigVersion:       $"saas.{version}",
            Dimensions:          dimDefs,
            ScoringRubric:       rubricDefs,
            DimensionWeights:    weightMap,
            Bands:               bandsConfig,
            PostureRules:        postureRules,
            SourceTiers:         tierMap,
            ClassificationRules: classifyRules,
            HalfLifeDays:        halfLifeMap,
            EntityResolution:    erConfig);
    }

    private static void Validate(SaasProfile p)
    {
        // 1. Dimension weights must exist and sum to ~1.0
        if (p.DimensionWeights.Count == 0)
            throw new InvalidOperationException("Catalogue: dimension_weights is empty.");
        var weightSum = p.DimensionWeights.Values.Sum();
        if (Math.Abs(weightSum - 1.0) > 0.001)
            throw new InvalidOperationException($"Catalogue: dimension weights sum to {weightSum:F4}, expected 1.0.");

        // 2. Every criterion referenced in classification rules must exist in scoring_rubric
        foreach (var rule in p.ClassificationRules)
            if (!p.ScoringRubric.ContainsKey(rule.Criterion))
                throw new InvalidOperationException(
                    $"Catalogue: classification rule references criterion '{rule.Criterion}' not found in scoring_rubric.");

        // 3. Every tier referenced in classification rules must exist in source_tiers
        foreach (var rule in p.ClassificationRules)
            if (!p.SourceTiers.ContainsKey(rule.Tier))
                throw new InvalidOperationException(
                    $"Catalogue: classification rule references tier '{rule.Tier}' not found in source_tiers.");

        // 4. Invariant #4: REPORTED tier weight must be permanently below the critical confidence gate
        if (p.SourceTiers.TryGetValue("Reported", out var reportedCfg)
            && reportedCfg.Weight >= p.Bands.CriticalConfidenceGate)
            throw new InvalidOperationException(
                $"Catalogue invariant #4 violated: Reported tier weight {reportedCfg.Weight} >= " +
                $"critical confidence gate {p.Bands.CriticalConfidenceGate}. " +
                "A single Reported signal must never be able to force CRITICAL.");

        // 5. Posture rules must reference valid stances
        var validStances = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "Maintain", "Monitor", "Renegotiate", "Escalate", "Remediate" };
        foreach (var rule in p.PostureRules)
            if (!validStances.Contains(rule.Stance))
                throw new InvalidOperationException($"Catalogue: posture rule has unknown stance '{rule.Stance}'.");
    }
}
