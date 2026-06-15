using Kozmo.Contracts;
using Kozmo.Contracts.Config;

namespace Ii.Contracts;

public interface IDecayEngine
{
    /// <summary>
    /// Compute the current freshness of a belief as of 'now'.
    /// Uses exponential half-life decay by source tier from the profile.
    /// No clock access — now is injected by Ii.Spine.
    /// </summary>
    double ComputeFreshness(Belief belief, SaasProfile profile, DateTimeOffset now);

    /// <summary>
    /// Return the belief with freshness and confidence recomputed for 'now'.
    /// Does NOT mutate the original — returns a new Belief record (same ID, updated fields).
    /// </summary>
    Belief WithCurrentFreshness(Belief belief, SaasProfile profile, DateTimeOffset now);
}
