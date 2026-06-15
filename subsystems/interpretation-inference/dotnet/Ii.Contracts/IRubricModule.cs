using Kozmo.Contracts;
using Kozmo.Contracts.Config;

namespace Ii.Contracts;

public interface IRubricModule
{
    /// <summary>
    /// Aggregate a set of current beliefs for a single dimension into a DimensionScore.
    /// Pure and deterministic — receives pre-decayed beliefs (confidence already reflects current freshness).
    /// </summary>
    DimensionScore ScoreDimension(
        Guid                  entityId,
        Dimension             dimension,
        IReadOnlyList<Belief> beliefs,
        SaasProfile           profile);
}
