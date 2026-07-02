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
        int halfLifeDays;
        if (belief.HalfLifeDays.HasValue)
        {
            // Vendor file belief: half-life from claim_key catalogue stored on the belief.
            // 0 = contractual, no decay.
            halfLifeDays = belief.HalfLifeDays.Value;
            if (halfLifeDays <= 0) return 1.0;
        }
        else
        {
            // Signal pipeline belief: half-life from tier config.
            var tierName = belief.SourceTier.ToString();
            if (!profile.HalfLifeDays.TryGetValue(tierName, out halfLifeDays) || halfLifeDays <= 0)
                return 1.0;
        }

        // Use observed_at when available (vendor file); fall back to created_at (signal pipeline).
        var baseTime = belief.ObservedAt ?? belief.CreatedAt;
        var ageDays  = Math.Max(0, (now - baseTime).TotalDays);

        return Math.Pow(2.0, -ageDays / halfLifeDays);
    }

    public Belief WithCurrentFreshness(Belief belief, SaasProfile profile, DateTimeOffset now)
    {
        var freshness = ComputeFreshness(belief, profile, now);
        // For vendor file beliefs (Freshness stored as 1.0 at creation, Confidence = capped extractor conf):
        //   base = stored_confidence / 1.0 = stored_confidence → new_confidence = stored_confidence × freshness
        // For signal beliefs (Confidence = tierWeight × freshness_at_creation):
        //   base = (tierWeight × f_c) / f_c = tierWeight → new_confidence = tierWeight × freshness_now
        // Both behave correctly via this formula.
        var base_conf  = belief.Freshness > 1e-10 ? belief.Confidence / belief.Freshness : TierWeight(belief.SourceTier, profile);
        var confidence = base_conf * freshness;
        return belief with { Freshness = freshness, Confidence = confidence };
    }

    private static double TierWeight(SourceTier tier, SaasProfile profile)
    {
        var key = tier.ToString();
        return profile.SourceTiers.TryGetValue(key, out var cfg) ? cfg.Weight : 0.0;
    }
}
