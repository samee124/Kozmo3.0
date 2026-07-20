namespace Po.VendorCall;

/// <summary>
/// Persistence interface for VendorCallRun lifecycle records.
/// Each record tracks one recognized vendor meeting from pre-meeting brief through post-meeting review.
/// </summary>
public interface IVendorCallRunStore
{
    /// <summary>Returns the run for the given calendar event ID, or null if not found.</summary>
    Task<VendorCallRun?> GetByEventIdAsync(string eventId, CancellationToken ct);

    /// <summary>Returns all runs with the given status, most recent first.</summary>
    Task<IReadOnlyList<VendorCallRun>> GetByStatusAsync(VendorCallStatus status, CancellationToken ct);

    /// <summary>Returns the run for the given run ID, or null if not found.</summary>
    Task<VendorCallRun?> GetByIdAsync(Guid id, CancellationToken ct);

    /// <summary>Returns all runs for the given vendor, most recent first.</summary>
    Task<IReadOnlyList<VendorCallRun>> GetByVendorIdAsync(Guid vendorId, CancellationToken ct);

    /// <summary>Upserts the run record (insert or replace by EventId).</summary>
    Task SaveAsync(VendorCallRun run, CancellationToken ct);
}
