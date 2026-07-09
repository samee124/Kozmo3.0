namespace Kozmo.Contracts.Config;

/// <summary>
/// Merged view of the nine *.saas.v1.json catalogue configs plus vendor file extensions.
/// Loaded once at startup by ICatalogue; immutable thereafter.
/// </summary>
public sealed record SaasProfile(
    string ConfigVersion,
    IReadOnlyDictionary<string, DimensionDefinition>  Dimensions,
    IReadOnlyDictionary<string, CriterionRubric>      ScoringRubric,
    IReadOnlyDictionary<string, double>               DimensionWeights,
    BandsConfig                                        Bands,
    IReadOnlyList<PostureRule>                         PostureRules,
    IReadOnlyDictionary<string, SourceTierConfig>     SourceTiers,
    IReadOnlyList<ClassificationRule>                  ClassificationRules,
    IReadOnlyDictionary<string, int>                  HalfLifeDays,   // key = SourceTier name
    EntityResolutionConfig                             EntityResolution
)
{
    // Vendor file extensions — non-null when loaded from profiles containing the config files
    public IReadOnlyDictionary<string, ClaimKeyDefinition> ClaimKeyCatalogue { get; init; } =
        new Dictionary<string, ClaimKeyDefinition>();

    public IReadOnlyDictionary<string, string> DocTypeTierMap { get; init; } =
        new Dictionary<string, string>();

    public IReadOnlyDictionary<string, IReadOnlyList<string>> ExpectedBeliefSets { get; init; } =
        new Dictionary<string, IReadOnlyList<string>>();

    // E1 Part 7 Step 3/5/6 — document-type -> extraction-schema mapping (vendor file extension).
    // DefaultExtractionSchema is used whenever a document's inferred type (DocTypeInferrer.
    // InferDocType) has no entry in ExtractionSchemas. Empty BeliefKeys when the config is absent
    // (pre-migration profile) — DocumentBeliefExtractor falls back to
    // BeliefExtractionPrompt.TargetCriteriaOrder in that case.
    public ExtractionSchema DefaultExtractionSchema { get; init; } =
        new(Array.Empty<string>(), Array.Empty<MetadataFieldGroup>());

    public IReadOnlyDictionary<string, ExtractionSchema> ExtractionSchemas { get; init; } =
        new Dictionary<string, ExtractionSchema>();

    // E1 Part 7 Step 5 — metadata field catalogue (vendor file extension). Parallel to
    // ClaimKeyCatalogue above but for retained, non-scored metadata fields (Km.Store.Metadata) —
    // never projected into belief scoring, never read by the scoring assemblies (CI wall lane).
    public IReadOnlyDictionary<string, MetadataFieldDefinition> MetadataFieldCatalogue { get; init; } =
        new Dictionary<string, MetadataFieldDefinition>();

    // E2.1 — non-fatal coherence findings from Catalogue's boot-time validator (e.g. a "scored"
    // claim key whose rubric criterion doesn't resolve). Logged at Load() time; also exposed here
    // so tests can assert on them without parsing console output. Empty when everything's coherent.
    public IReadOnlyList<string> ValidationWarnings { get; init; } = Array.Empty<string>();
}

/// <summary>
/// A document type's extraction schema: which belief claim keys to ask about (scored, feed
/// Rubric/Index, extracted in ONE isolated pass — see E1 Part 7 Step 6) and which metadata field
/// GROUPS to ask about (retained, never scored, one LLM pass per group). Both empty for an
/// unclassified/pre-migration schema.
/// </summary>
public sealed record ExtractionSchema(
    IReadOnlyList<string>            BeliefKeys,
    IReadOnlyList<MetadataFieldGroup> MetadataFieldGroups
);

/// <summary>
/// E1 Part 7 Step 6 — a thematically-coherent group of ~4-5 metadata fields, extracted together
/// in ONE LLM pass. Grouping exists because a single pass asking about all of a document type's
/// metadata fields at once (18 for MSA) gave near-zero recall even on documents that verifiably
/// state most of them — the model can't reliably hold that many categories in attention at once.
/// Splitting into small, coherent groups (proven: a 5-field group hit 100% recall; an 8-field
/// group hit 38% and missed fields present in the source text) restores recall. The grouping is
/// config, not code, so any document type's metadata depth is a catalogue change away.
/// </summary>
public sealed record MetadataFieldGroup(
    string                Name,
    IReadOnlyList<string> Fields
);

/// <summary>
/// One row in the metadata field catalogue (E1 Part 7 Step 5) — a retained, structured,
/// non-scored fact type (a contract clause, an obligation) distinct from a claim key. Same
/// fragment-driven prompt-generation shape as ClaimKeyDefinition, minus the scoring-specific
/// fields (no class/value_type/dimension/tier/half-life — metadata carries none of those).
/// </summary>
public sealed record MetadataFieldDefinition(
    string Definition,
    string PositiveExample,
    string NegativeExample,
    string PromptFragment
);

public sealed record DimensionDefinition(
    string Description,
    IReadOnlyList<string> Criteria
);

public sealed record CriterionRubric(
    string Type,            // "numeric" | "enum"
    IReadOnlyList<RubricThreshold>? NumericThresholds,
    IReadOnlyDictionary<string, double>? EnumScores
);

public sealed record RubricThreshold(double Min, double Max, double Score);

public sealed record BandsConfig(
    double HealthyMin,              // composite >= HealthyMin → Healthy
    double AtRiskMin,               // composite >= AtRiskMin  → AtRisk (else Critical, subject to confidence gate)
    double CriticalConfidenceGate,  // confidence_floor must be >= this to assign Critical
    double PerContradictionPenalty, // posture confidence penalty per detected contradiction
    double PerGapPenalty            // posture confidence penalty per evidence gap (must be ≤ PerContradictionPenalty)
);

public sealed record PostureRule(
    string Band,
    string Pattern,         // "Improving" | "Stable" | "Declining" | "any"
    int?   RenewalWithinDays,
    string Stance
);

public sealed record SourceTierConfig(
    double Weight,
    string Description
)
{
    /// <summary>
    /// Maximum confidence a vendor file extractor may assign for this tier.
    /// confidence = min(extractor_confidence, Ceiling). Defaults to Weight if not set in catalogue.
    /// </summary>
    public double Ceiling { get; init; }
}

/// <summary>Vendor file: one row in the claim_key catalogue (§4 of the spec).</summary>
public sealed record ClaimKeyDefinition(
    string   ClaimClass,       // "scored" | "structural"
    string   ValueType,        // "percent" | "rating" | "metric" | "score" | "enum" | "money" | "date" | "duration" | "bool"
    string   Dimension,        // Operational | Experiential | Financial | Strategic | "" for structural
    string   TypicalTier,      // e.g. "VERIFIED"
    int?     HalfLifeDays,     // null = no decay (contractual)
    double   DimensionWeight   // 0.25 for equal-weighted; 0 for structural
)
{
    // E1 Part 7 Step 1 additions — extraction-prompt generation source (E1 Part 7 Step 2).
    // Absent/empty for claim keys not yet migrated onto the extended schema.
    public string  Definition          { get; init; } = "";
    public string  PositiveExample     { get; init; } = "";
    public string  NegativeExample     { get; init; } = "";
    public string? DeterministicGuard  { get; init; }

    // E1 Part 7 Step 2: the exact original BeliefExtractionPrompt.System wording for this key,
    // byte-for-byte — not a paraphrase of Definition/PositiveExample/NegativeExample above. This
    // is what BeliefExtractionPrompt.BuildSystem projects, so the generated prompt reproduces the
    // hand-authored prompt exactly (same cassette cache keys, zero re-record risk). Empty for
    // claim keys not projected into the extraction prompt.
    public string PromptFragment { get; init; } = "";

    // E1 Part 7 Step 7 Fix 4 — the scoring_rubric.saas.v1.json key this claim key bands against,
    // when it differs from the claim key itself (e.g. sla_uptime -> uptime_sla, csat -> csat_score).
    // Null when the claim key name matches its rubric criterion name exactly. Single source of
    // truth for the claim-key -> rubric-criterion translation, replacing the two independent
    // hardcoded dictionaries previously in RulesExtractor and BeliefPersistenceStage.
    public string? RubricCriterion { get; init; }

    // E1 Part 7 Step 7 Fix 2 — vendor classes for which this claim key is an expected slot.
    // E2.1: no longer the authority Catalogue.cs derives ExpectedBeliefSets from — Requirement
    // (below) is now the authority. Kept, unused for behavior, only as the input to Catalogue's
    // boot-time coherence check (the Requirement-derived expected set must still equal this
    // ExpectedFor-derived set) — a one-version safety net before this field is removed entirely.
    public IReadOnlyList<string> ExpectedFor { get; init; } = Array.Empty<string>();

    // E2.1 — additive metadata only; no consumer branches on this yet (E2.3 wires it into
    // dimension-assessability gating). "required" | "expected" | "optional". Populated to exactly
    // reproduce today's ExpectedFor-derived behavior: every key that was in expected_for is
    // "expected"; every key that wasn't is "optional"; nothing is "required" yet (that would change
    // scoring-gate behavior, E2.1 must not). Catalogue.cs derives ExpectedBeliefSets from this field.
    public string Requirement { get; init; } = "optional";
};

public sealed record ClassificationRule(
    string SourceSystem,
    string PayloadKey,
    string Dimension,
    string Criterion,
    string Tier
);

public sealed record EntityResolutionConfig(
    string Strategy,
    double FuzzyThreshold,
    IReadOnlyDictionary<string, string> AliasMap
);
