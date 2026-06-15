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
);
