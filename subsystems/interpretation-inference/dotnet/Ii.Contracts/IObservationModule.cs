using Kozmo.Contracts;
using Kozmo.Contracts.Config;

namespace Ii.Contracts;

public interface IObservationModule
{
    /// <summary>
    /// Classify a signal into (Dimension, Criterion, Value 0-1, SourceTier) using the catalogue rules.
    /// Returns null if no classification rule matches the signal.
    /// </summary>
    ClassificationResult? Classify(Signal signal, SaasProfile profile);

    /// <summary>
    /// Resolve a raw entity reference (possibly an alias) to the canonical entity ID.
    /// </summary>
    Guid ResolveEntity(string entityRef, Guid fallbackEntityId, SaasProfile profile);
}

public sealed record ClassificationResult(
    Dimension  Dimension,
    string     Criterion,
    double     Value,       // 0–1 normalised rubric score
    SourceTier SourceTier,
    string     Derivation
)
{
    // ── Annotation fields — NOT fingerprint inputs ──────────────────────────

    /// <summary>How this classification was produced. Defaults to Rule.</summary>
    public ClassificationMethod Method { get; init; } = ClassificationMethod.Rule;

    /// <summary>Raw model confidence (0–1) before tier×freshness fold-in. Null for rule path.</summary>
    public double? MethodConfidence { get; init; } = null;

    /// <summary>LLM reasoning text when Method == Llm. Null for rule-classified results.</summary>
    public string? ReasoningSummary { get; init; } = null;
}
