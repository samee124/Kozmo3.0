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
}

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
);

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
