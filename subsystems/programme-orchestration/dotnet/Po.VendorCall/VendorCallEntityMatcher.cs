namespace Po.VendorCall;

/// <summary>
/// Resolves external attendee email domains to canonical vendor entities.
/// Cascade: domain exact (0.95) → name exact (0.85) → alias (0.80) → unmatched.
/// </summary>
public sealed class VendorCallEntityMatcher
{
    private readonly IVendorLookup _lookup;

    public VendorCallEntityMatcher(IVendorLookup lookup)
        => _lookup = lookup;

    /// <summary>
    /// Matches each unique external-attendee domain against the vendor registry.
    /// Returns one result per distinct domain (deduplicated by VendorId when matched).
    /// </summary>
    public async Task<IReadOnlyList<VendorEntityMatchResult>> MatchAsync(
        IReadOnlyList<string> externalAttendeeEmails,
        CancellationToken     ct = default)
    {
        var results = new List<VendorEntityMatchResult>();

        var domains = externalAttendeeEmails
            .Select(ExtractDomain)
            .Where(d => d is not null)
            .Select(d => d!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var domain in domains)
        {
            // 1. Domain exact
            var byDomain = await _lookup.FindByDomainAsync(domain, ct);
            if (byDomain.Count > 0)
            {
                foreach (var m in byDomain)
                    AddIfNew(results, new VendorEntityMatchResult(m.VendorId, m.VendorName, VendorMatchType.DomainExact, 0.95));
                continue;
            }

            // 2. Name exact (use the stem of the domain as a candidate name)
            var stem    = DomainStem(domain);
            var byName  = await _lookup.FindByNameAsync(stem, ct);
            if (byName.Count > 0)
            {
                foreach (var m in byName)
                    AddIfNew(results, new VendorEntityMatchResult(m.VendorId, m.VendorName, VendorMatchType.NameExact, 0.85));
                continue;
            }

            // 3. Alias
            var byAlias = await _lookup.FindByAliasAsync(stem, ct);
            if (byAlias.Count > 0)
            {
                foreach (var m in byAlias)
                    AddIfNew(results, new VendorEntityMatchResult(m.VendorId, m.VendorName, VendorMatchType.Alias, 0.80));
                continue;
            }

            // 4. Unmatched — domain is the best identity we have
            results.Add(new VendorEntityMatchResult(Guid.Empty, domain, VendorMatchType.Unmatched, 0.0));
        }

        return results;
    }

    private static void AddIfNew(List<VendorEntityMatchResult> list, VendorEntityMatchResult item)
    {
        if (item.VendorId != Guid.Empty && list.Any(r => r.VendorId == item.VendorId))
            return;
        list.Add(item);
    }

    private static string? ExtractDomain(string email)
    {
        var at = email.IndexOf('@');
        return at >= 0 ? email[(at + 1)..] : null;
    }

    /// <summary>Strips the last DNS label (TLD) to get a company-name hint.</summary>
    private static string DomainStem(string domain)
    {
        var dot = domain.LastIndexOf('.');
        return dot > 0 ? domain[..dot] : domain;
    }
}
