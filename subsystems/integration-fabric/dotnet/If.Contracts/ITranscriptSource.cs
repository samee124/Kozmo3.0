namespace If.Contracts;

/// <summary>
/// Integration-neutral interface for resolving meetings and fetching their transcripts.
/// Implementations handle authentication, retries, and rate-limiting internally.
/// The interface is Microsoft-free — any meeting platform can implement it.
/// </summary>
public interface ITranscriptSource
{
    /// <summary>Resolves a meeting join URL to a meeting identifier the transcript system understands.</summary>
    Task<MeetingResolutionResult> ResolveMeetingAsync(string joinWebUrl, CancellationToken ct);

    /// <summary>Checks whether a transcript exists and is ready for a resolved meeting.</summary>
    Task<TranscriptAvailabilityResult> CheckTranscriptAvailabilityAsync(string meetingId, CancellationToken ct);

    /// <summary>Downloads and returns the raw transcript content.</summary>
    Task<TranscriptContent> FetchTranscriptAsync(string meetingId, string transcriptId, CancellationToken ct);
}

public sealed record MeetingResolutionResult(
    bool    Resolved,
    string? MeetingId,
    string? FailureReason);

public sealed record TranscriptAvailabilityResult(
    bool            Available,
    string?         TranscriptId,
    DateTimeOffset? CreatedAt,
    string?         FailureReason);

public sealed record TranscriptContent(
    string         TranscriptId,
    string         MeetingId,
    string         Format,         // "text/vtt"
    string         RawContent,     // the full VTT text
    DateTimeOffset FetchedAt);
