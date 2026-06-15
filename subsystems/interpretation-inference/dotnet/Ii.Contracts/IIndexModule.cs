using Kozmo.Contracts;
using Kozmo.Contracts.Config;

namespace Ii.Contracts;

public interface IIndexModule
{
    /// <summary>
    /// Aggregate all four DimensionScores into an EntityIndex with composite, band, and fingerprint.
    /// Time is provided by the caller (Ii.Spine) — never read the clock internally.
    /// </summary>
    EntityIndex Aggregate(
        Guid                                          entityId,
        IReadOnlyDictionary<Dimension, DimensionScore> dimensionScores,
        IReadOnlyList<Belief>                         allBeliefs,
        EntityIndex?                                  previous,
        SaasProfile                                   profile,
        DateTimeOffset                                now);

    /// <summary>
    /// Recompute only the dimension that changed; copy remaining scores from previous.
    /// Must produce the same result as a full Aggregate call (asserted in determinism spike).
    /// </summary>
    EntityIndex RecomputeDirty(
        Guid                                          entityId,
        Dimension                                     dirtyDimension,
        DimensionScore                                newDimensionScore,
        IReadOnlyList<Belief>                         allBeliefs,
        EntityIndex                                   previous,
        SaasProfile                                   profile,
        DateTimeOffset                                now);

    /// <summary>Compute the fingerprint for a given set of inputs (exposed for spike tests).</summary>
    string ComputeFingerprint(
        IReadOnlyDictionary<Dimension, DimensionScore> dimensionScores,
        IReadOnlyList<Belief>                          allBeliefs,
        SaasProfile                                    profile);
}
