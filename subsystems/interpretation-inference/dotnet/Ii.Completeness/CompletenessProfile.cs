namespace Ii.Completeness;

/// <summary>
/// The completeness snapshot for a vendor at a given depth level.
/// Deterministic: same answer set → same profile. Never stored — recomputed on demand.
/// </summary>
public sealed record CompletenessProfile(
    IReadOnlyList<DimensionCoverage> DimensionCoverages,
    IReadOnlyList<string>            AnsweredQuestionIds,
    IReadOnlyList<string>            GapQuestionIds)
{
    /// <summary>Overall coverage across all selected dimensions.</summary>
    public double OverallCoverage
    {
        get
        {
            var required = DimensionCoverages.Sum(d => d.RequiredCount);
            var answered = DimensionCoverages.Sum(d => d.AnsweredCount);
            return required > 0 ? (double)answered / required : 0.0;
        }
    }
}
