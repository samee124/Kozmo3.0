namespace Ig.Contracts;

/// <summary>
/// The three buckets Stage E gates every cluster into.
/// Same structural pattern as Band in the II pipeline (Healthy/AtRisk/Critical)
/// but with identity-specific thresholds (AUTO_CONFIRM_MIN from IdentityGateConfig).
/// </summary>
public enum Disposition { AutoConfirm, Provisional, Triage, NonVendor }

/// <summary>
/// Output of Stage E per cluster — the Phase 3 seam.
/// Phase 3 (check-in loop) consumes triage_reason + triage_question unchanged;
/// it does not re-derive or reformat them.
/// </summary>
public sealed record ResolutionDisposition(
    Guid                  ClusterId,
    IReadOnlyList<Guid>   MemberCandidateIds,
    string                ProposedCanonicalName,
    string                ComparisonKey,
    EntityType            EntityType,
    Disposition           Disposition,
    double                Confidence,
    IReadOnlyList<string> Flags,
    string?               TriageReason,    // non-null iff Disposition == Triage
    string?               TriageQuestion   // fully-formed human-readable question for Phase 3
);
