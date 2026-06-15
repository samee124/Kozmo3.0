using Kozmo.Contracts;
using Kozmo.Contracts.Config;

namespace Ii.Contracts;

public interface IPostureModule
{
    /// <summary>
    /// Assign a stance from band + trend pattern + renewal proximity.
    /// Deterministic — same inputs always produce the same stance.
    /// <paramref name="meta"/> is optional; when non-null the module applies the confidence
    /// penalty (Clamp(index.ConfidenceFloor - 0.1 * contradictionCount, 0, 0.95)) and
    /// surfaces Cautions / EvidenceGaps on the returned assignment.
    /// </summary>
    PostureAssignment Assign(
        EntityIndex          index,
        EntityIndex?         previousIndex,
        DateTimeOffset?      contractRenewalDate,
        SaasProfile          profile,
        DateTimeOffset       now,
        MetaCognitionResult? meta = null);
}
