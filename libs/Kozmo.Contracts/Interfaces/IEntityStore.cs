namespace Kozmo.Contracts.Interfaces;

/// <summary>
/// Append-and-supersede knowledge store. No edit or delete path exists — by design.
/// </summary>
public interface IEntityStore
{
    // Beliefs — append only
    Task AppendBeliefAsync(Belief belief, CancellationToken ct = default);
    Task<IReadOnlyList<Belief>> GetCurrentBeliefsAsync(Guid entityId, CancellationToken ct = default);
    Task<IReadOnlyList<Belief>> GetBeliefHistoryAsync(Guid entityId, CancellationToken ct = default);

    // Entity index — save replaces; full history kept
    Task SaveIndexAsync(EntityIndex index, CancellationToken ct = default);
    Task<EntityIndex?> GetIndexAsync(Guid entityId, CancellationToken ct = default);
    Task<IReadOnlyList<EntityIndex>> GetIndexHistoryAsync(Guid entityId, CancellationToken ct = default);

    // Posture — append only
    Task AppendPostureAsync(PostureAssignment posture, CancellationToken ct = default);
    Task<PostureAssignment?> GetCurrentPostureAsync(Guid entityId, CancellationToken ct = default);

    // Signals — append only
    Task AppendSignalAsync(Signal signal, CancellationToken ct = default);
    Task<Signal?> GetSignalAsync(Guid signalId, CancellationToken ct = default);

    // Posture history — read-only; used by trajectory endpoint
    Task<IReadOnlyList<PostureAssignment>> GetPostureHistoryAsync(Guid entityId, CancellationToken ct = default);

    // Signal history for entity — ordered by received_at; used to correlate index versions
    Task<IReadOnlyList<Signal>> GetSignalsForEntityAsync(Guid entityId, CancellationToken ct = default);

    // Reset — test/demo harness only
    Task ResetAsync(CancellationToken ct = default);
}
