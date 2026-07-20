namespace Po.VendorCall;

/// <summary>In-memory IReviewCheckpointStore for unit tests.</summary>
public sealed class InMemoryReviewCheckpointStore : IReviewCheckpointStore
{
    private readonly List<ReviewCheckpoint> _checkpoints = [];

    public Task<ReviewCheckpoint?> GetLatestAsync(Guid vendorId, CancellationToken ct)
    {
        var result = _checkpoints
            .Where(c => c.VendorId == vendorId)
            .OrderByDescending(c => c.CreatedAtUtc)
            .FirstOrDefault();
        return Task.FromResult(result);
    }

    public Task<IReadOnlyList<ReviewCheckpoint>> GetHistoryAsync(
        Guid vendorId, int maxCount, CancellationToken ct)
    {
        IReadOnlyList<ReviewCheckpoint> result = _checkpoints
            .Where(c => c.VendorId == vendorId)
            .OrderByDescending(c => c.CreatedAtUtc)
            .Take(maxCount)
            .ToList();
        return Task.FromResult(result);
    }

    public Task SaveAsync(ReviewCheckpoint checkpoint, CancellationToken ct)
    {
        _checkpoints.Add(checkpoint);
        return Task.CompletedTask;
    }
}
