namespace Po.VendorCall;

/// <summary>
/// A structured pre-meeting vendor briefing produced by VendorCallBriefingComposer.
/// Every section has source references — no statement is unsourced.
/// </summary>
public sealed record VendorCallBriefing(
    string                        VendorName,
    DateTimeOffset                MeetingTime,
    string                        MeetingSubject,
    IReadOnlyList<string>         Attendees,

    BriefingSection               MeetingObjective,
    BriefingSection               ContractPosition,
    BriefingSection               RecentDevelopments,
    BriefingSection               OpenCommitments,
    BriefingSection               RisksAndOpportunities,
    BriefingSection               EvidenceGaps,
    BriefingSection               RecommendedQuestions,
    BriefingSection               SafestNextAction,

    IReadOnlyList<BriefingCitation> Citations);

/// <summary>A single named section of the briefing.</summary>
public sealed record BriefingSection(
    string                Heading,
    string                Content,
    /// <summary>
    /// Source IDs (EvidenceId strings or email ExternalIds) that back this section.
    /// The text renderer resolves these to citation indices [N] from the Citations list.
    /// </summary>
    IReadOnlyList<string> SourceReferences);

/// <summary>A numbered source citation collected from all sections.</summary>
public sealed record BriefingCitation(
    int            Index,
    string         SourceDescription,
    string         SourceId,
    DateTimeOffset SourceDate);
