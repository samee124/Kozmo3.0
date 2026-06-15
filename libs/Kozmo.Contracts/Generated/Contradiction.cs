// GENERATED — do not hand-edit; regenerate via tools/codegen/generate.ps1

namespace Kozmo.Contracts;

/// <summary>
/// A detected conflict between two or more beliefs about the same entity/dimension.
/// Lowers posture confidence; does not change stance (anti-proliferation rule).
/// Annotation only — NOT a fingerprint input.
/// </summary>
public readonly record struct Contradiction(
    string                EntityId,
    string                Dimension,
    string                Description,
    ContradictionSeverity Severity,
    IReadOnlyList<Guid>   ConflictingBeliefIds,
    DetectionSource       DetectedBy);
