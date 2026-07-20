namespace Po.VendorCall;

/// <summary>Represents a vendor entry returned by a lookup.</summary>
public sealed record VendorMatch(
    Guid                   VendorId,
    string                 VendorName,
    IReadOnlyList<string>  KnownDomains,
    IReadOnlyList<string>  Aliases);

/// <summary>
/// Vendor data access seam used by VendorCallEntityMatcher.
/// Implementations may be in-memory (tests) or backed by an entity store.
/// </summary>
public interface IVendorLookup
{
    Task<IReadOnlyList<VendorMatch>> FindByDomainAsync(string domain,  CancellationToken ct = default);
    Task<IReadOnlyList<VendorMatch>> FindByNameAsync  (string name,    CancellationToken ct = default);
    Task<IReadOnlyList<VendorMatch>> FindByAliasAsync (string alias,   CancellationToken ct = default);
}
