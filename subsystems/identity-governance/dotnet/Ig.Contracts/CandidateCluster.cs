namespace Ig.Contracts;

/// <summary>
/// Output of Stage C (Cluster) + Stage D (CollisionFlagging).
/// One cluster per distinct canonical vendor candidate.
/// Flags from §4 of the spec are set by Stage D.
/// </summary>
public sealed record CandidateCluster(
    Guid                            ClusterId,
    IReadOnlyList<ClassifiedCandidate> Members,
    string                          CanonicalName,
    string                          ComparisonKey,
    EntityType                      EntityType,
    double                          Confidence,
    IReadOnlyList<string>           Flags,
    string                          EntityRole    // "vendor"|"customer"|"issuer"|"internal"|"unknown"
);
