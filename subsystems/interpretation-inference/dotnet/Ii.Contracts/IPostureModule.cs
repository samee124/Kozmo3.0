using Kozmo.Contracts;
using Kozmo.Contracts.Config;

namespace Ii.Contracts;

public interface IPostureModule
{
    /// <summary>
    /// Assign a stance from band + trend pattern + renewal proximity.
    /// Deterministic — same inputs always produce the same stance.
    /// </summary>
    PostureAssignment Assign(
        EntityIndex    index,
        EntityIndex?   previousIndex,
        DateTimeOffset? contractRenewalDate,
        SaasProfile    profile,
        DateTimeOffset now);
}
