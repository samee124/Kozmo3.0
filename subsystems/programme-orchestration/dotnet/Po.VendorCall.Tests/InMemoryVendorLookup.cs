using Po.VendorCall;

namespace Po.VendorCall.Tests;

/// <summary>Test double for IVendorLookup — backed by a list of VendorMatch entries.</summary>
internal sealed class InMemoryVendorLookup : IVendorLookup
{
    private readonly IReadOnlyList<VendorMatch> _vendors;

    public InMemoryVendorLookup(IReadOnlyList<VendorMatch> vendors)
        => _vendors = vendors;

    public Task<IReadOnlyList<VendorMatch>> FindByDomainAsync(string domain, CancellationToken ct = default)
    {
        IReadOnlyList<VendorMatch> matches = _vendors
            .Where(v => v.KnownDomains.Any(d => d.Equals(domain, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        return Task.FromResult(matches);
    }

    public Task<IReadOnlyList<VendorMatch>> FindByNameAsync(string name, CancellationToken ct = default)
    {
        IReadOnlyList<VendorMatch> matches = _vendors
            .Where(v => v.VendorName.Equals(name, StringComparison.OrdinalIgnoreCase))
            .ToList();
        return Task.FromResult(matches);
    }

    public Task<IReadOnlyList<VendorMatch>> FindByAliasAsync(string alias, CancellationToken ct = default)
    {
        IReadOnlyList<VendorMatch> matches = _vendors
            .Where(v => v.Aliases.Any(a => a.Equals(alias, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        return Task.FromResult(matches);
    }
}
