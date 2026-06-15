// GENERATED — do not hand-edit; regenerate via tools/codegen/generate.ps1

namespace Kozmo.Contracts;

/// <summary>
/// A detected absence of evidence for a dimension/criterion.
/// Surfaces in the drill-down as "conflicting evidence — verify before acting".
/// Annotation only — NOT a fingerprint input.
/// </summary>
public readonly record struct Gap(
    string          EntityId,
    string          Dimension,
    string          Description,
    DetectionSource DetectedBy);
