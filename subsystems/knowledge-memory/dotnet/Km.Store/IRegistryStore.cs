namespace Km.Store;

/// <summary>
/// Storage interface for the Identity & Governance Registry (canonical vendors + aliases).
/// Uses primitive/BCL types only so Km.Store carries no dependency on Ig.Contracts.
/// Implemented by SqliteEntityStore (shared connection, shared schema).
/// </summary>
public interface IRegistryStore
{
    Task SaveRegistryVendorAsync(RegistryVendorRow vendor, CancellationToken ct = default);
    Task SaveVendorAliasAsync(VendorAliasRow alias, CancellationToken ct = default);
    Task<RegistryVendorRow?> GetRegistryVendorAsync(Guid vendorId, CancellationToken ct = default);
    Task<IReadOnlyList<VendorAliasRow>> GetVendorAliasesAsync(Guid vendorId, CancellationToken ct = default);
    Task<IReadOnlyList<RegistryVendorRow>> GetAllRegistryVendorsAsync(CancellationToken ct = default);
}

/// <summary>Storage row for a canonical vendor (vendors table — identity columns).</summary>
public sealed record RegistryVendorRow(
    Guid           VendorId,
    string         CanonicalName,
    DateTimeOffset CreatedAt,
    string?        ComparisonKey,
    string?        EntityType,
    double?        Confidence,
    string?        FlagsJson,
    string?        Status,
    string?        RebrandMapRef,
    string?        AcquisitionMapRef,
    Guid?          AbsorbedIntoVendorId = null,
    Guid?          ProgramRunId         = null,
    string?        EntityRole           = null
);

/// <summary>
/// Storage row for a Program (programs table) — a durable container that can span multiple
/// ingestion runs. NOT one-run-equals-one-program: a run (program_run_id) is a single ingestion
/// event that belongs to a Program (program_id).
/// </summary>
public sealed record ProgramRow(
    Guid           Id,
    string         Name,
    DateTimeOffset CreatedAt
);

/// <summary>Storage row for a vendor alias (vendor_aliases table).</summary>
public sealed record VendorAliasRow(
    Guid    AliasId,
    Guid    VendorId,
    string  RawName,
    string? ProvenanceDocId,
    string? ProvenanceSpan
);
