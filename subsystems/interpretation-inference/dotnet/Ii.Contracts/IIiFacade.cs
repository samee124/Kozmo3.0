using Kozmo.Contracts;

namespace Ii.Contracts;

/// <summary>
/// Contract 2 — the I&amp;I façade. Intake and UI build against this interface only.
/// Frozen before fan-out; changes require joint sign-off.
/// </summary>
public interface IIiFacade
{
    /// <summary>Submit a raw signal. Returns the trace ID for the operation.</summary>
    Task<Guid> SubmitSignalAsync(Signal signal, CancellationToken ct = default);

    /// <summary>Get the current posture assignment for an entity.</summary>
    Task<PostureAssignment?> GetPostureAsync(Guid entityId, CancellationToken ct = default);

    /// <summary>Get the current multi-dimensional index (scores, composite, band, fingerprint).</summary>
    Task<EntityIndex?> GetIndexAsync(Guid entityId, CancellationToken ct = default);

    /// <summary>Get all current (non-superseded) beliefs for an entity.</summary>
    Task<IReadOnlyList<Belief>> GetBeliefsAsync(Guid entityId, CancellationToken ct = default);

    /// <summary>
    /// Get the full reasoning trail: Posture ← Band ← Index ← Beliefs ← Signals.
    /// The glass-box chain for the drill-down view.
    /// </summary>
    Task<ReasoningTrail?> GetReasoningTrailAsync(Guid entityId, CancellationToken ct = default);

    /// <summary>
    /// Get the ordered history of (index version, posture, signal) tuples for the chart.
    /// One point per processed signal; ordered by timestamp ascending.
    /// </summary>
    Task<IReadOnlyList<TrajectoryPoint>> GetTrajectoryAsync(Guid entityId, CancellationToken ct = default);

    /// <summary>Reset all state. Demo/test harness only.</summary>
    Task ResetAsync(CancellationToken ct = default);
}

public sealed record TrajectoryPoint(
    DateTimeOffset Timestamp,
    Guid?          SignalId,
    double         Composite,
    Band           Band,
    Stance         Stance,
    string         Fingerprint
);

public sealed record ReasoningTrail(
    Guid               EntityId,
    PostureAssignment? Posture,
    EntityIndex?       Index,
    IReadOnlyList<Belief>  CurrentBeliefs,
    IReadOnlyList<Signal>  SourceSignals
)
{
    /// <summary>
    /// MetaCognition result for this entity (STEP 1 — B4).
    /// Populated by GetReasoningTrailAsync; null when entity has no data yet.
    /// Annotation only — NOT a fingerprint input.
    /// </summary>
    public MetaCognitionResult? Meta { get; init; } = null;
}
