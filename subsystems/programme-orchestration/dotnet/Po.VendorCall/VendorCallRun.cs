namespace Po.VendorCall;

/// <summary>
/// Central lifecycle record for one recognized vendor meeting.
/// Created during the pre-meeting pipeline (Phase 7) and updated through
/// transcript fetching, analysis, and post-meeting review (Phases 9+).
/// One VendorCallRun per recognized vendor meeting — identified by EventId.
/// </summary>
public sealed class VendorCallRun
{
    public Guid           Id                      { get; init; }
    public string         EventId                 { get; init; } = "";   // Graph calendar event ID
    public string?        ICalUid                 { get; init; }
    public string?        JoinWebUrl              { get; init; }          // Teams meeting URL — bridge to transcripts
    public Guid           VendorId                { get; init; }
    public string         VendorName              { get; init; } = "";
    public string         MeetingSubject          { get; init; } = "";
    public DateTimeOffset StartUtc                { get; init; }
    public DateTimeOffset EndUtc                  { get; init; }
    public string         SignedInUserPrincipalId { get; init; } = "";

    // Lifecycle state
    public VendorCallStatus Status { get; set; }

    // Pre-meeting tracking
    public DateTimeOffset? PreCheckInSentAt { get; set; }
    public DateTimeOffset? BriefingSentAt   { get; set; }
    public string?         PreCheckpointId  { get; set; }

    // Post-meeting tracking
    public string?         OnlineMeetingId      { get; set; }  // resolved from JoinWebUrl
    public string?         TranscriptId         { get; set; }
    public DateTimeOffset? TranscriptFetchedAt  { get; set; }
    public DateTimeOffset? TranscriptAnalyzedAt { get; set; }
    public DateTimeOffset? PostSummarySentAt    { get; set; }
    public string?         PostCheckpointId     { get; set; }
    public string?         ReviewToken          { get; set; }  // one-time token for review page
    public DateTimeOffset? ReviewTokenExpiresAt { get; set; }  // 48 h from generation
    public string?         SummaryJson          { get; set; }  // serialized PostMeetingSummary (Phase 9c+)

    // Timestamps
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public enum VendorCallStatus
{
    Detected,            // calendar event recognized as vendor meeting
    PreCheckInSent,      // pre-meeting check-in dispatched
    BriefingSent,        // pre-meeting brief emailed
    MeetingEnded,        // end time + buffer passed
    TranscriptPending,   // waiting for transcript to become available
    TranscriptReady,     // transcript fetched
    TranscriptAnalyzed,  // LLM extraction complete (Phase 9b)
    PostSummarySent,     // post-meeting email delivered (Phase 9c)
    AwaitingUserReview,   // user has received the review link (Phase 9d)
    Closed,              // user confirmed, checkpoint saved (Phase 9e)
    PostCheckInSent,      // post-meeting check-in dispatched (EnableTranscriptAnalysis=false)
    NoTranscriptAvailable, // transcript unavailable or not enabled for this meeting
    Cancelled,             // meeting was cancelled / deleted from the calendar
}
