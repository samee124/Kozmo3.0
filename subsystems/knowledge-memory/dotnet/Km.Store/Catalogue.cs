using System.Text.Json;
using System.Text.Json.Nodes;
using Kozmo.Contracts;
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

        // Vendor file extensions — optional; silently absent pre-migration
        var claimKeys    = TryLoadJson(dir, "claim_key_catalogue.saas.v1.json");
        var docTypeMap   = TryLoadJson(dir, "doc_type_tier_map.saas.v1.json");
        var extractionSchemas = TryLoadJson(dir, "extraction_schemas.saas.v1.json");
        var metadataFields    = TryLoadJson(dir, "metadata_field_catalogue.saas.v1.json");

        var profile = Assemble(dims, rubric, weights, bands, postures, tiers, classify, decay, entityRes,
                               claimKeys, docTypeMap, extractionSchemas, metadataFields);
        var warnings = Validate(profile);
        return profile with { ValidationWarnings = warnings };
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

    private static JsonObject? TryLoadJson(string dir, string file)
    {
        var path = Path.Combine(dir, file);
        if (!File.Exists(path)) return null;
        var text = File.ReadAllText(path);
        return JsonNode.Parse(text)?.AsObject();
    }

    private static SaasProfile Assemble(
        JsonObject dims, JsonObject rubric, JsonObject weights, JsonObject bands,
        JsonObject postures, JsonObject tiers, JsonObject classify, JsonObject decay, JsonObject entityRes,
        JsonObject? claimKeys, JsonObject? docTypeMap, JsonObject? extractionSchemas,
        JsonObject? metadataFields)
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
            var to     = kv.Value!.AsObject();
            var weight = to["weight"]!.GetValue<double>();
            var ceiling = to["ceiling"]?.GetValue<double>() ?? weight;  // default ceiling = weight
            tierMap[kv.Key] = new SourceTierConfig(weight, to["description"]?.GetValue<string>() ?? "")
            {
                Ceiling = ceiling
            };
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

        // Claim key catalogue (vendor file extension)
        var claimKeyDefs = new Dictionary<string, ClaimKeyDefinition>();
        if (claimKeys != null)
        {
            foreach (var kv in claimKeys["claim_keys"]!.AsObject())
            {
                var ck  = kv.Value!.AsObject();
                var hld = ck["half_life_days"]?.GetValue<int?>();
                var expectedFor = ck["expected_for"]?.AsArray()
                    .Select(x => x!.GetValue<string>())
                    .ToList() ?? new List<string>();
                claimKeyDefs[kv.Key] = new ClaimKeyDefinition(
                    ClaimClass:     ck["class"]!.GetValue<string>(),
                    ValueType:      ck["value_type"]!.GetValue<string>(),
                    Dimension:      ck["dimension"]?.GetValue<string>() ?? "",
                    TypicalTier:    ck["typical_tier"]?.GetValue<string>() ?? "",
                    HalfLifeDays:   hld,
                    DimensionWeight: ck["dimension_weight"]?.GetValue<double>() ?? 0.0)
                {
                    Definition         = ck["definition"]?.GetValue<string>() ?? "",
                    PositiveExample    = ck["positive_example"]?.GetValue<string>() ?? "",
                    NegativeExample    = ck["negative_example"]?.GetValue<string>() ?? "",
                    DeterministicGuard = ck["deterministic_guard"]?.GetValue<string>(),
                    PromptFragment     = ck["prompt_fragment"]?.GetValue<string>() ?? "",
                    RubricCriterion    = ck["rubric_criterion"]?.GetValue<string>(),
                    ExpectedFor        = expectedFor,
                    // E2.1 — defaults to "optional" for a pre-migration config missing the field
                    // (preserves prior behavior: absent expected_for -> not counted).
                    Requirement        = ck["requirement"]?.GetValue<string>() ?? "optional"
                };
            }
        }

        // Doc-type → tier map (vendor file extension)
        var docTypeTierMap = new Dictionary<string, string>();
        if (docTypeMap != null)
            foreach (var kv in docTypeMap["map"]!.AsObject())
                docTypeTierMap[kv.Key] = kv.Value!.GetValue<string>();

        // Expected belief sets (E2.1) — derived from each claim key's `requirement` field
        // (required | expected -> counted; optional -> not), not from `expected_for` anymore.
        // expected_for is retained on ClaimKeyDefinition only as the input to the boot-time
        // coherence check in Validate() below, which hard-fails if this derivation ever disagrees
        // with the legacy expected_for-derived set — the guarantee that this migration changed
        // nothing behaviorally.
        //
        // Single vendor class ("saas_vendor"): this catalogue file has only ever served one vendor
        // class — every populated expected_for tag today is exactly ["saas_vendor"], never another
        // class, never more than one. Requirement is not vendor-class-scoped (a doctrine covers one
        // type by construction — see kozmo_e2_unified_doctrine_spec.md's per-type catalogue-file
        // design), so the derived set is placed under that same single class. Multi-vendor-type
        // catalogues are a separate, later piece of E2 (a distinct catalogue file per type), not
        // something this derivation needs to anticipate.
        const string SingleVendorClass = "saas_vendor";
        var expectedBeliefSets = new Dictionary<string, IReadOnlyList<string>>();
        var requirementExpectedKeys = claimKeyDefs
            .Where(kv => kv.Value.Requirement is "required" or "expected")
            .Select(kv => kv.Key)
            .ToList();
        if (requirementExpectedKeys.Count > 0)
            expectedBeliefSets[SingleVendorClass] = requirementExpectedKeys;

        // Document-type -> extraction-schema mapping (vendor file extension, E1 Part 7 Step 3/5/6)
        var defaultSchema       = new ExtractionSchema(Array.Empty<string>(), Array.Empty<MetadataFieldGroup>());
        var extractionSchemaMap = new Dictionary<string, ExtractionSchema>();
        if (extractionSchemas != null)
        {
            defaultSchema = ParseExtractionSchema(extractionSchemas["default"]!.AsObject());

            foreach (var kv in extractionSchemas["doc_type_schemas"]!.AsObject())
                extractionSchemaMap[kv.Key] = ParseExtractionSchema(kv.Value!.AsObject());
        }

        // Metadata field catalogue (vendor file extension, E1 Part 7 Step 5)
        var metadataFieldDefs = new Dictionary<string, MetadataFieldDefinition>();
        if (metadataFields != null)
        {
            foreach (var kv in metadataFields["metadata_fields"]!.AsObject())
            {
                var mf = kv.Value!.AsObject();
                metadataFieldDefs[kv.Key] = new MetadataFieldDefinition(
                    Definition:      mf["definition"]?.GetValue<string>() ?? "",
                    PositiveExample: mf["positive_example"]?.GetValue<string>() ?? "",
                    NegativeExample: mf["negative_example"]?.GetValue<string>() ?? "",
                    PromptFragment:  mf["prompt_fragment"]?.GetValue<string>() ?? "");
            }
        }

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
            EntityResolution:    erConfig)
        {
            ClaimKeyCatalogue      = claimKeyDefs,
            DocTypeTierMap         = docTypeTierMap,
            ExpectedBeliefSets     = expectedBeliefSets,
            DefaultExtractionSchema = defaultSchema,
            ExtractionSchemas      = extractionSchemaMap,
            MetadataFieldCatalogue = metadataFieldDefs
        };
    }

    private static ExtractionSchema ParseExtractionSchema(JsonObject schemaObj)
    {
        var beliefKeys = schemaObj["claim_keys"]!.AsArray()
            .Select(x => x!.GetValue<string>())
            .ToList();
        var metadataFieldGroups = schemaObj["metadata_field_groups"]!.AsArray()
            .Select(g =>
            {
                var go = g!.AsObject();
                var fields = go["fields"]!.AsArray().Select(x => x!.GetValue<string>()).ToList();
                return new MetadataFieldGroup(go["name"]!.GetValue<string>(), fields);
            })
            .ToList();
        return new ExtractionSchema(beliefKeys, metadataFieldGroups);
    }

    private static IReadOnlyList<string> Validate(SaasProfile p)
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

        // 4b. Vendor file gate: INFERRED ceiling must also be below the critical confidence gate
        if (p.SourceTiers.TryGetValue("Inferred", out var inferredCfg)
            && inferredCfg.Ceiling >= p.Bands.CriticalConfidenceGate)
            throw new InvalidOperationException(
                $"Catalogue vendor-file invariant violated: Inferred tier ceiling {inferredCfg.Ceiling} >= " +
                $"critical confidence gate {p.Bands.CriticalConfidenceGate}. " +
                "INFERRED evidence must never force Critical on its own.");

        // 5. Posture rules must reference valid stances
        var validStances = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "Maintain", "Monitor", "Renegotiate", "Escalate", "Remediate" };
        foreach (var rule in p.PostureRules)
            if (!validStances.Contains(rule.Stance))
                throw new InvalidOperationException($"Catalogue: posture rule has unknown stance '{rule.Stance}'.");

        // ── E2.1 unified-doctrine coherence checks ──────────────────────────────────────────────

        var warnings = new List<string>();

        // 6. WARNING (not hard-fail — E2.3's job to fix): every "scored" claim key's effective
        // rubric criterion (RubricCriterion, falling back to the claim key's own name — the same
        // fallback ObservationModule.ScoreFromRubric callers use everywhere) must resolve in
        // scoring_rubric.saas.v1.json. A scored key whose criterion doesn't resolve will never
        // produce a score — a silent dead claim key. Catches usage_trend today.
        foreach (var (key, ckDef) in p.ClaimKeyCatalogue)
        {
            if (ckDef.ClaimClass != "scored") continue;
            var effectiveCriterion = ckDef.RubricCriterion ?? key;
            if (!p.ScoringRubric.ContainsKey(effectiveCriterion))
            {
                warnings.Add(
                    $"claim key '{key}' is class=scored but its rubric criterion '{effectiveCriterion}' " +
                    "does not exist in scoring_rubric.saas.v1.json — it will never produce a score.");
                continue; // the membership check below is moot if the criterion doesn't exist at all
            }

            // 7. WARNING: a scored key's effective criterion should also be listed under its own
            // declared dimension in dimensions.saas.v1.json (criterion registry membership) —
            // catches a key that scores fine but whose dimension registration has drifted.
            if (!string.IsNullOrEmpty(ckDef.Dimension)
                && p.Dimensions.TryGetValue(ckDef.Dimension, out var dimDef)
                && !dimDef.Criteria.Contains(effectiveCriterion, StringComparer.OrdinalIgnoreCase))
            {
                warnings.Add(
                    $"claim key '{key}' declares dimension '{ckDef.Dimension}' but its rubric criterion " +
                    $"'{effectiveCriterion}' is not listed in dimensions.saas.v1.json's " +
                    $"'{ckDef.Dimension}'.criteria — dimension/rubric registration has drifted.");
            }
        }

        // 8. WARNING: every claim key's non-empty dimension must be a known dimension at all
        // (distinct, coarser check than #7's membership check).
        foreach (var (key, ckDef) in p.ClaimKeyCatalogue)
            if (!string.IsNullOrEmpty(ckDef.Dimension) && !p.Dimensions.ContainsKey(ckDef.Dimension))
                warnings.Add(
                    $"claim key '{key}' declares dimension '{ckDef.Dimension}', which does not exist " +
                    "in dimensions.saas.v1.json.");

        // 9. HARD FAIL — migration coherence: the Requirement-derived ExpectedBeliefSets must equal
        // the legacy ExpectedFor-derived set exactly. A mismatch means this migration changed
        // behavior, which E2.1 must never do.
        var legacyExpected = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var (key, ckDef) in p.ClaimKeyCatalogue)
            foreach (var vendorClass in ckDef.ExpectedFor)
            {
                if (!legacyExpected.TryGetValue(vendorClass, out var list))
                    legacyExpected[vendorClass] = list = new List<string>();
                list.Add(key);
            }

        if (legacyExpected.Count != p.ExpectedBeliefSets.Count)
            throw new InvalidOperationException(
                $"Catalogue coherence: requirement-derived expected_belief_sets has " +
                $"{p.ExpectedBeliefSets.Count} vendor class(es) but the legacy expected_for-derived " +
                $"set has {legacyExpected.Count}. The requirement migration changed behavior.");

        foreach (var (vendorClass, legacyKeys) in legacyExpected)
        {
            if (!p.ExpectedBeliefSets.TryGetValue(vendorClass, out var newKeys))
                throw new InvalidOperationException(
                    $"Catalogue coherence: vendor class '{vendorClass}' is in the legacy " +
                    "expected_for-derived set but missing from the requirement-derived one.");

            var legacySet = legacyKeys.ToHashSet(StringComparer.Ordinal);
            var newSet    = newKeys.ToHashSet(StringComparer.Ordinal);
            if (!legacySet.SetEquals(newSet))
                throw new InvalidOperationException(
                    $"Catalogue coherence: expected_belief_sets['{vendorClass}'] differs between the " +
                    $"requirement-derived set and the legacy expected_for-derived set. " +
                    $"Legacy: [{string.Join(", ", legacySet.OrderBy(x => x, StringComparer.Ordinal))}]. " +
                    $"New: [{string.Join(", ", newSet.OrderBy(x => x, StringComparer.Ordinal))}].");
        }

        // 10. HARD FAIL: every extraction schema's claim_keys entry must name a real catalogue
        // claim key. BeliefExtractionPrompt.BuildSystem / EmailInterpretationPrompt.BuildBeliefSystem
        // already throw on an unknown key — but only lazily, the first time that specific doc-type
        // (or email) schema is actually used for a real extraction. This surfaces the identical
        // error at boot, for every schema, unconditionally — an unknown key should never ship
        // waiting to be discovered days later by whichever document type happens to trip it first.
        ValidateExtractionSchemaKeys(p);

        foreach (var w in warnings)
            Console.WriteLine($"[Catalogue] WARNING: {w}");

        return warnings;
    }

    /// <summary>
    /// E2.2a — public and pure (profile in, throws or returns) so it's independently testable
    /// without going through file-based Load(). Called from Validate() above for the real config.
    /// </summary>
    public static void ValidateExtractionSchemaKeys(SaasProfile p)
    {
        CheckSchema("default", p.DefaultExtractionSchema.BeliefKeys);
        foreach (var (schemaName, schema) in p.ExtractionSchemas)
            CheckSchema(schemaName, schema.BeliefKeys);

        void CheckSchema(string schemaName, IReadOnlyList<string> beliefKeys)
        {
            foreach (var key in beliefKeys)
                if (!p.ClaimKeyCatalogue.ContainsKey(key))
                    throw new InvalidOperationException(
                        $"Catalogue coherence: extraction schema '{schemaName}' references claim key " +
                        $"'{key}', which does not exist in claim_key_catalogue.saas.v1.json.");
        }
    }
}
