namespace Po.VendorCall;

/// <summary>
/// Immutable snapshot of the five-question review answer set for one vendor, at one point in time.
/// Saved after every pre- or post-meeting review cycle so the next review can compare against it.
/// </summary>
public sealed record ReviewCheckpoint(
    Guid                   Id,
    Guid                   VendorId,
    Guid?                  VendorCallRunId,        // null if not tied to a specific meeting
    CheckpointKind         Kind,
    DateTimeOffset         CreatedAtUtc,
    ReviewStatus           Status,
    ReviewMovement         Movement,
    ReviewConfidence       Confidence,
    string                 Q1Answer,
    string                 Q2Answer,
    string                 Q3Answer,
    string                 Q4Answer,
    string                 Q5Answer,
    int                    OpenCommitmentCount,
    int                    OverdueCommitmentCount,
    int                    UnresolvedSignalCount,
    IReadOnlyList<string>  SourceReferenceIds);    // evidence IDs cited across all 5 answers

public enum CheckpointKind   { PreMeeting, PostMeeting }
public enum ReviewStatus     { Green, Amber, Red }
public enum ReviewMovement   { Improving, Stable, Weakening }
public enum ReviewConfidence { High, Medium, Low }
