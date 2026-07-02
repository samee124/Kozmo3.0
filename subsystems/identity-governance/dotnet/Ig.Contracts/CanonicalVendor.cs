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
    Guid?                 AbsorbedIntoVendorId = null
);
