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
        if (beliefs.Count == 0)
            return new DimensionScore(entityId, dimension, 0.5, 0.0, []);

        var totalWeight = beliefs.Sum(b => b.Confidence);

        double score = totalWeight > 0
            ? beliefs.Sum(b => b.Value * b.Confidence) / totalWeight
            : beliefs.Average(b => b.Value);

        var confidence      = beliefs.Max(b => b.Confidence);
        var contributingIds = beliefs.Select(b => b.Id).ToList();

        return new DimensionScore(entityId, dimension, score, confidence, contributingIds);
    }
}
