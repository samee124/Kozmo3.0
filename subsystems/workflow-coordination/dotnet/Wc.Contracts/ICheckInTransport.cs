namespace Wc.Contracts;

/// <summary>
/// Transport interface (§3). Simulated in Phase 3 (in-app pending list);
/// real email is a later swap — same interface, different implementation.
/// Accepts a batch: all check-ins in one call are delivered in one digest (one email envelope).
/// Grouping by (VendorId, ProgramRunId) is the caller's responsibility — each group maps to
/// one SendAsync call, producing one email per vendor per run instead of one email per question.
/// </summary>
public interface ICheckInTransport
{
    Task SendAsync(IReadOnlyList<CheckIn> checkIns, CancellationToken ct = default);
}
