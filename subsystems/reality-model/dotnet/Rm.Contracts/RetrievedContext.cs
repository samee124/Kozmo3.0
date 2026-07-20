using Kozmo.Contracts;

namespace Rm.Contracts;

/// <summary>
/// All facts retrieved from the existing read model for a single vendor + aspect.
/// This is the grounding layer: every factual claim in VendorQueryAnswer.Text must trace
/// to a field here. Callers and tests can verify the prose against the structured data.
/// </summary>
public sealed record RetrievedContext(
    Guid                              VendorId,
    string                            VendorName,

    /// <summary>False when the vendor is known but has no scored index/posture yet.</summary>
    bool                              IsAssessed,

    /// <summary>Null when IsAssessed = false.</summary>
    PostureAssignment?                Posture,

    /// <summary>Null when IsAssessed = false.</summary>
    EntityIndex?                      Index,

    /// <summary>Current (non-superseded) beliefs. Populated for Full and Evidence aspects.</summary>
    IReadOnlyList<Belief>             Beliefs,

    /// <summary>Detected contradictions with severity. Populated for Full and Contradictions aspects.</summary>
    IReadOnlyList<Contradiction>      Contradictions,

    /// <summary>Detected gaps. Populated for Full and Gaps aspects.</summary>
    IReadOnlyList<Gap>                Gaps,

    /// <summary>LLM-generated epistemic summary from the meta-cognition pass. May be null.</summary>
    string?                           EpistemicSummary,

    /// <summary>Open check-in questions awaiting owner response. Populated for Full and Gaps aspects.</summary>
    IReadOnlyList<OpenCheckInSummary> OpenCheckIns,

    /// <summary>
    /// Future seam for access-control. Passed through the retrieve step but never acted on in this phase.
    /// </summary>
    string?                           CallerContext = null,

    /// <summary>
    /// When non-null, this context was scoped to a single dimension.
    /// Beliefs, Gaps, and Contradictions contain only data for that dimension.
    /// The composer uses this to phrase a dimension-focused answer.
    /// </summary>
    Dimension?                        FilterDimension = null
);

/// <summary>Lightweight projection of an open check-in for grounding purposes.</summary>
public sealed record OpenCheckInSummary(Guid CheckInId, string Question, string Kind);