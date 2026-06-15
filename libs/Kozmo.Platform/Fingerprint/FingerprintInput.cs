namespace Kozmo.Platform.Fingerprint;

/// <summary>
/// The four inputs hashed into a reproducible fingerprint.
/// Sort order is enforced by FingerprintComputer before serialisation.
/// </summary>
public sealed record FingerprintInput(
    IReadOnlyList<BeliefSnapshot>           Beliefs,
    IReadOnlyDictionary<string, double>     DimensionScores,
    IReadOnlyDictionary<string, double>     DimensionWeights,
    string                                  ConfigVersion
);

/// <summary>
/// Stable projection of a Belief for hashing.
/// Id is intentionally omitted — the fingerprint captures evidence values, not storage keys.
/// </summary>
public sealed record BeliefSnapshot(
    string Dimension,
    string Criterion,
    double Value,
    double Confidence
);
