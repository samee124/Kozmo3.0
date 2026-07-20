namespace Po.VendorCall;

/// <summary>
/// Captures a user's review of the post-meeting summary.
/// Saved when the user submits the review page (/vendor-calls/{runId}/review).
/// </summary>
public sealed record PostMeetingReviewSubmission(
    Guid                          RunId,
    DateTimeOffset                SubmittedAt,
    bool                          SummaryAccurate,
    IReadOnlyList<ItemCorrection> Corrections,
    string?                       Additions,
    bool                          PromoteToEvidence);

/// <summary>
/// A user-supplied correction to one summary line item.
/// SectionKey identifies the section (e.g. "decisions", "commitments").
/// ItemIndex is the zero-based position of the item within that section.
/// </summary>
public sealed record ItemCorrection(
    string SectionKey,
    int    ItemIndex,
    string OriginalText,
    string EditedText);
