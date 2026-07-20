namespace Po.VendorCall;

/// <summary>In-memory implementation of IPostMeetingReviewStore for use in tests.</summary>
public sealed class InMemoryPostMeetingReviewStore : IPostMeetingReviewStore
{
    private readonly Dictionary<Guid, PostMeetingReviewSubmission> _store = new();

    public Task SaveAsync(PostMeetingReviewSubmission submission, CancellationToken ct)
    {
        _store[submission.RunId] = submission;
        return Task.CompletedTask;
    }

    public Task<PostMeetingReviewSubmission?> GetByRunIdAsync(Guid runId, CancellationToken ct)
        => Task.FromResult(_store.TryGetValue(runId, out var sub) ? sub : null);
}
