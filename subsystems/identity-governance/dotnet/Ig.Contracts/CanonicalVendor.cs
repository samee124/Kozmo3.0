namespace Ig.Contracts;

public enum EntityType
{
    Company,
    Person,
    Product,
    Internal,
    NonVendor,
    Unknown,
}

public enum RegistryStatus
{
    Confirmed,
    Provisional,
    Triage,
    Absorbed,
}

public sealed record VendorAlias(
    Guid    AliasId,
    Guid    VendorId,
    string  RawName,
    string? ProvenanceDocId,
    string? ProvenanceSpan
);

public sealed record CanonicalVendor(
    Guid                  VendorId,
    string                CanonicalName,
    IReadOnlyList<VendorAlias> Aliases,
    string?               ComparisonKey,
    EntityType            EntityType,
    double                Confidence,
    IReadOnlyList<string> Flags,
    RegistryStatus        Status,
    string?               RebrandMapRef,
    string?               AcquisitionMapRef,
    DateTimeOffset        CreatedAt,
    Guid?                 AbsorbedIntoVendorId = null,
    // "vendor"|"customer"|"issuer"|"internal"|"unknown" — Stage C's CandidateCluster.EntityRole,
    // carried through instead of discarded (E-signal Part 5 Step 3). Stage E already used this to
    // gate NonVendor disposition before this field existed; this is the same value, now persisted
    // alongside the vendor instead of thrown away after the gate check. Null for any vendor
    // resolved before this field existed (legacy rows).
    string?               EntityRole = null
);
