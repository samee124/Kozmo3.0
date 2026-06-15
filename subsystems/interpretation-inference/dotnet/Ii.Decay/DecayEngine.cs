using Ii.Contracts;
using Kozmo.Contracts;
using Kozmo.Contracts.Config;

namespace Ii.Decay;

/// <summary>
/// Exponential half-life freshness decay by source tier.
/// Pure and deterministic — receives 'now' from Ii.Spine, never reads the clock.
/// </summary>
public sealed class DecayEngine : IDecayEngine
{
    public double ComputeFreshness(Belief belief, SaasProfile profile, DateTimeOffset now)
    {
        var tierName = belief.SourceTier.ToString();
        if (!profile.HalfLifeDays.TryGetValue(tierName, out var halfLifeDays) || halfLifeDays <= 0)
            return 1.0;

        var ageDays = (now - belief.CreatedAt).TotalDays;
        if (ageDays < 0) ageDays = 0;

        return Math.Pow(2.0, -ageDays / halfLifeDays);
    }

    public Belief WithCurrentFreshness(Belief belief, SaasProfile profile, DateTimeOffset now)
    {
        var freshness   = ComputeFreshness(belief, profile, now);
        var tierWeight  = TierWeight(belief.SourceTier, profile);
        var confidence  = tierWeight * freshness;
        return belief with { Freshness = freshness, Confidence = confidence };
    }

    private static double TierWeight(SourceTier tier, SaasProfile profile)
    {
        var key = tier.ToString();
        return profile.SourceTiers.TryGetValue(key, out var cfg) ? cfg.Weight : 0.0;
    }
}
