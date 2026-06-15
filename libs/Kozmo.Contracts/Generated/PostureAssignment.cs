// GENERATED — do not hand-edit; regenerate via tools/codegen/generate.ps1

namespace Kozmo.Contracts;

/// <summary>
/// Confidence rule (applied in Posture module, Phase 1+):
///   posture.Confidence = Clamp(index.ConfidenceFloor - 0.1 * activeContradictionCount, 0.0, 0.95)
/// Cautions and EvidenceGaps are annotation — not fingerprint inputs.
/// </summary>
public sealed record PostureAssignment(
    Guid           Id,
    Guid           EntityId,
    Band           Band,
    Stance         Stance,
    string         Rationale,
    IReadOnlyList<string> EvidenceTrail,
    double         Confidence,
    string         Fingerprint,
    int            IndexVersion,
    DateTimeOffset AssignedAt,
    DateTimeOffset? ValidUntil
)
{
    // ── Meta-cognition surface — annotation only, not fingerprint inputs ──────

    /// <summary>Human-readable contradiction descriptions from MetaCognitionResult. Empty in Phase 0.</summary>
    public IReadOnlyList<string> Cautions { get; init; } = [];

    /// <summary>Human-readable evidence gap descriptions from MetaCognitionResult. Empty in Phase 0.</summary>
    public IReadOnlyList<string> EvidenceGaps { get; init; } = [];
}
