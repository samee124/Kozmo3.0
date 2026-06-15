// GENERATED — do not hand-edit; regenerate via tools/codegen/generate.ps1

namespace Kozmo.Contracts;

/// <summary>
/// Output of the meta-cognition pass (Phase 1+).
/// Annotation only — NOT a fingerprint input, NOT a stance input.
/// Contradictions and gaps surface in PostureAssignment.Cautions / EvidenceGaps for the drill-down.
/// </summary>
public readonly record struct MetaCognitionResult(
    string                      EntityId,
    IReadOnlyList<Contradiction> Contradictions,
    IReadOnlyList<Gap>           Gaps,
    string                      EpistemicSummary);
