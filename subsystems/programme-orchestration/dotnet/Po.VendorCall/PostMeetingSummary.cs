namespace Po.VendorCall;

/// <summary>
/// Structured post-meeting summary produced by PostMeetingSummaryComposer.
/// Every section has source references — no statement is unsourced.
/// </summary>
public sealed record PostMeetingSummary(
    string                          VendorName,
    DateTimeOffset                  MeetingTime,
    string                          MeetingSubject,
    IReadOnlyList<string>           Attendees,
    SummarySection                  MeetingOutcome,
    SummarySection                  DecisionsMade,
    SummarySection                  NewCommitments,
    SummarySection                  ResolvedFromPreBrief,
    SummarySection                  CommercialStateChange,
    SummarySection                  StillOpen,
    SummarySection                  RecommendedNextAction,
    IReadOnlyList<SummaryCitation>  Citations);

/// <summary>A single named section of the post-meeting summary.</summary>
public sealed record SummarySection(
    string                        Heading,
    string                        Content,
    IReadOnlyList<SummaryLineItem> Items,
    IReadOnlyList<string>         SourceReferences);

/// <summary>One evidence-backed line item within a summary section.</summary>
public sealed record SummaryLineItem(
    string  Text,
    string? Speaker,
    string? Owner,
    string? DueDate,
    string? TranscriptTimestamp,
    double  Confidence,
    bool    RequiresUserConfirmation,
    string  SourceReference);

/// <summary>A numbered source citation collected from all sections.</summary>
public sealed record SummaryCitation(
    int             Index,
    string          SourceDescription,
    string?         TranscriptTimestamp,
    DateTimeOffset? SourceDate);
