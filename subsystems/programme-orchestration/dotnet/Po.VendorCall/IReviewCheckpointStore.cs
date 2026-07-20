namespace Po.VendorCall;

public interface IReviewCheckpointStore
{
    /// <summary>
    /// Returns the most recent checkpoint for the vendor regardless of Kind,
    /// or null if none exists yet.
    /// </summary>
    Task<ReviewCheckpoint?> GetLatestAsync(Guid vendorId, CancellationToken ct);

    /// <summary>Returns up to <paramref name="maxCount"/> checkpoints in descending date order.</summary>
    Task<IReadOnlyList<ReviewCheckpoint>> GetHistoryAsync(
        Guid vendorId, int maxCount, CancellationToken ct);

    Task SaveAsync(ReviewCheckpoint checkpoint, CancellationToken ct);
}
