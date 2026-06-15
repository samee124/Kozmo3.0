// GENERATED — do not hand-edit; regenerate via tools/codegen/generate.ps1

namespace Kozmo.Contracts;

public sealed record DimensionScore(
    Guid           EntityId,
    Dimension      Dimension,
    double         Score,          // 0–1 weighted aggregate
    double         Confidence,     // max confidence among contributing beliefs
    IReadOnlyList<Guid> ContributingBeliefIds
);
