namespace Ig.Contracts;

/// <summary>
/// Output of Stage B (Entity-type classification).
/// IsDropped=true for PERSON / INTERNAL / PRODUCT / NON_VENDOR;
/// COMPANY and UNKNOWN proceed to Stage C clustering.
/// DropReason is non-null iff IsDropped=true.
/// </summary>
public sealed record ClassifiedCandidate(
    NormalizedCandidate Normalized,
    EntityType          EntityType,
    bool                IsDropped,
    string?             DropReason
);
