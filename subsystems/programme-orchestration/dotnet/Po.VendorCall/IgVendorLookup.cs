using Ig.Contracts;

namespace Po.VendorCall;

/// <summary>
/// Adapts IIdentityRegistry (the SQLite-backed canonical vendor store) to the IVendorLookup
/// interface consumed by VendorCallEntityMatcher.
/// </summary>
public sealed class IgVendorLookup : IVendorLookup
{
    private readonly IIdentityRegistry _registry;

    public IgVendorLookup(IIdentityRegistry registry)
        => _registry = registry;

    public async Task<IReadOnlyList<VendorMatch>> FindByDomainAsync(string domain, CancellationToken ct = default)
    {
        var all = await _registry.GetAllAsync(ct);
        return all
            .Where(v => v.KnownDomains.Any(d => d.Equals(domain, StringComparison.OrdinalIgnoreCase)))
            .Select(ToMatch)
            .ToList();
    }

    public async Task<IReadOnlyList<VendorMatch>> FindByNameAsync(string name, CancellationToken ct = default)
    {
        var all = await _registry.GetAllAsync(ct);
        return all
            .Where(v => v.CanonicalName.Equals(name, StringComparison.OrdinalIgnoreCase))
            .Select(ToMatch)
            .ToList();
    }

    public async Task<IReadOnlyList<VendorMatch>> FindByAliasAsync(string alias, CancellationToken ct = default)
    {
        var all = await _registry.GetAllAsync(ct);
        return all
            .Where(v => v.Aliases.Any(a => a.RawName.Equals(alias, StringComparison.OrdinalIgnoreCase)))
            .Select(ToMatch)
            .ToList();
    }

    private static VendorMatch ToMatch(CanonicalVendor v) => new(
        VendorId:    v.VendorId,
        VendorName:  v.CanonicalName,
        KnownDomains: v.KnownDomains,
        Aliases:     v.Aliases.Select(a => a.RawName).ToList());
}
