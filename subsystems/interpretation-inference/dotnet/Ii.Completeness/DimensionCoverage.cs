using Kozmo.Contracts;

namespace Ii.Completeness;

/// <summary>
/// Per-dimension completeness coverage for a vendor.
/// Coverage = AnsweredCount / RequiredCount (0.0 when RequiredCount is zero).
/// </summary>
public sealed record DimensionCoverage(
    Dimension Dimension,
    int       AnsweredCount,
    int       RequiredCount)
{
    public double Coverage => RequiredCount > 0 ? (double)AnsweredCount / RequiredCount : 0.0;
}
