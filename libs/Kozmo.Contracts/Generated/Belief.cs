// GENERATED — do not hand-edit; regenerate via tools/codegen/generate.ps1

namespace Kozmo.Contracts;

/// <summary>
/// KnowledgeTuple — immutable unit of structured knowledge. Append-and-supersede only; no edit path exists.
///
/// Fingerprint inputs (decision-relevant): Dimension, Criterion, Value, Confidence.
/// Annotation fields (NOT fingerprint inputs): ClassificationMethod, ClassificationConfidence, ReasoningSummary.
/// </summary>
public sealed record Belief(
    Guid           Id,
    Guid           EntityId,
    Dimension      Dimension,
    string         Criterion,
    double         Value,          // normalised 0–1 rubric score
    SourceTier     SourceTier,
    double         Confidence,     // tier_weight × current_freshness  ← FINGERPRINT INPUT
    double         Freshness,      // freshness at creation time
    string         Derivation,
    IReadOnlyList<Guid> SourceSignals,
    int            Version,
    Guid?          SupersededBy,   // null on current version; set when superseded
    DateTimeOffset CreatedAt,
    Guid           TraceId
)
{
    // ── Annotation fields — NOT fingerprint inputs ────────────────────────────

    /// <summary>How this belief was classified. Default Rule for Phase 0 deterministic path.</summary>
    public ClassificationMethod ClassificationMethod { get; init; } = ClassificationMethod.Rule;

    /// <summary>Raw method confidence before tier×freshness fold-in. Null for pure rule classification.</summary>
    public double? ClassificationConfidence { get; init; } = null;

    /// <summary>LLM rationale when ClassificationMethod == Llm. Null for rule-classified beliefs.</summary>
    public string? ReasoningSummary { get; init; } = null;
}
