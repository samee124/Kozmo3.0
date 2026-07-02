namespace Wc.Contracts;

public interface ICheckInStore
{
    Task SaveAsync(CheckIn checkIn, CancellationToken ct = default);
    Task<IReadOnlyList<CheckIn>> GetOpenAsync(CancellationToken ct = default);
    Task<CheckIn?> GetAsync(Guid checkInId, CancellationToken ct = default);

    /// <summary>
    /// Returns all PROCESSED and EXPIRED check-ins for a vendor, ordered by raised_at.
    /// Used by the completeness convergence loop to determine whether a gap question has
    /// been "tried and resolved" — the trigger for promoting it to a permanent gap.
    /// </summary>
    Task<IReadOnlyList<CheckIn>> GetResolvedForVendorAsync(Guid vendorId, CancellationToken ct = default);
}
