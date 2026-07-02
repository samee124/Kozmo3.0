namespace Ig.Contracts;

/// <summary>
/// Output of Stage A (Normalize). ComparisonKey is the deterministic matching key;
/// EffectiveName is RawName with any document-title prefix stripped (casing preserved),
/// used by Stage B for entity-type pattern matching.
/// </summary>
public sealed record NormalizedCandidate(
    CandidateIdentityBelief Candidate,
    string                  ComparisonKey,
    string                  EffectiveName
);
