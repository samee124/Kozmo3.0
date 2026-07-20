namespace Po.VendorCall;

/// <summary>Persistence interface for post-meeting review submissions.</summary>
public interface IPostMeetingReviewStore
{
    /// <summary>Upserts the submission. One submission per run (keyed by RunId).</summary>
    Task SaveAsync(PostMeetingReviewSubmission submission, CancellationToken ct);

    /// <summary>Returns the submission for the given run, or null if none submitted yet.</summary>
    Task<PostMeetingReviewSubmission?> GetByRunIdAsync(Guid runId, CancellationToken ct);
}
