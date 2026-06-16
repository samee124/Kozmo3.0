namespace Kozmo.Contracts.Config;

/// <summary>
/// Merged view of the nine *.saas.v1.json catalogue configs.
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
