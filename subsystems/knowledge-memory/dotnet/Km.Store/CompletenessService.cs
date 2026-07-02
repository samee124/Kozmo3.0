using Kozmo.Contracts;
using Kozmo.Contracts.Config;

namespace Km.Store;

/// <summary>
/// Completeness scoring for vendor file (§8 of spec).
/// completeness = |filled expected claim_keys| / |expected claim_keys|
/// A gap is an expected claim_key with no active belief.
/// </summary>
public sealed class CompletenessService
{
    private readonly SaasProfile _profile;

    public CompletenessService(SaasProfile profile)
    {
        _profile = profile;
    }

    /// <summary>
    /// Compute completeness for a vendor given their current active beliefs.
    /// Returns a result with the ratio, filled slots, and gap list.
    /// </summary>
    public CompletenessResult Compute(
        Guid                    vendorId,
        IReadOnlyList<Belief>   currentBeliefs,
        string                  vendorClass = "saas_vendor")
    {
        if (!_profile.ExpectedBeliefSets.TryGetValue(vendorClass, out var expected))
            return new CompletenessResult(vendorId, 1.0, [], []);

        var filledKeys = currentBeliefs
            .Where(b => !string.IsNullOrEmpty(b.ClaimKey))
            .Select(b => b.ClaimKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var filled = expected.Where(k => filledKeys.Contains(k)).ToList();
        var gaps   = expected.Where(k => !filledKeys.Contains(k)).ToList();

        var ratio = expected.Count > 0 ? (double)filled.Count / expected.Count : 1.0;

        return new CompletenessResult(vendorId, ratio, filled, gaps);
    }
}

public sealed record CompletenessResult(
    Guid                   VendorId,
    double                 Ratio,          // 0.0–1.0
    IReadOnlyList<string>  FilledKeys,
    IReadOnlyList<string>  GapKeys
);
