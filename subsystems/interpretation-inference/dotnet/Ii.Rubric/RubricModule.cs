using Ii.Contracts;
using Kozmo.Contracts;
using Kozmo.Contracts.Config;

namespace Ii.Rubric;

/// <summary>
/// Aggregates current beliefs for a single dimension into a DimensionScore.
/// Pure weighted average: score = Î£(value Ã— confidence) / Î£(confidence).
/// When no beliefs exist, returns a neutral score (0.5) with zero confidence.
/// </summary>
public sealed class RubricModule : IRubricModule
{
    public DimensionScore ScoreDimension(
        Guid                  entityId,
        Dimension             dimension,
        IReadOnlyList<Belief> beliefs,
        SaasProfile           profile)
    {
        // Structural vendor-file beliefs carry Confidence=0 and do not feed dimension scoring.
        var scoredBeliefs = beliefs.Where(b => b.Confidence > 0).ToList();
        if (scoredBeliefs.Count == 0)
            return new DimensionScore(entityId, dimension, 0.5, 0.0, []);

        var totalWeight = scoredBeliefs.Sum(b => b.Confidence);

        double score = totalWeight > 0
            ? scoredBeliefs.Sum(b => b.Value * b.Confidence) / totalWeight
            : scoredBeliefs.Average(b => b.Value);

        var confidence      = scoredBeliefs.Max(b => b.Confidence);
        var contributingIds = scoredBeliefs.Select(b => b.Id).ToList();

        return new DimensionScore(entityId, dimension, score, confidence, contributingIds);
    }
}
