using Kozmo.Contracts;
using Kozmo.Contracts.Config;

namespace Kozmo.Platform.Analysis;

/// <summary>
/// Applies the per-tier confidence ceiling from the SaasProfile catalogue.
/// Returns the lesser of the provided confidence and the tier ceiling.
/// </summary>
public static class ConfidenceClamper
{
    public static double Clamp(double confidence, SourceTier tier, SaasProfile profile) =>
        profile.SourceTiers.TryGetValue(tier.ToString(), out var tc)
            ? Math.Min(confidence, tc.Ceiling)
            : confidence;
}
