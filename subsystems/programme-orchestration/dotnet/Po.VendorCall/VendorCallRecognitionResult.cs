namespace Po.VendorCall;

/// <summary>
/// Output of VendorCallRecognizer.Recognize — fully deterministic given the same inputs.
/// </summary>
public sealed record VendorCallRecognitionResult(
    bool                  IsRelevant,
    bool                  RequiresReview,
    double                Confidence,
    IReadOnlyList<string> ExternalAttendees,
    IReadOnlyList<string> MatchedTitleTerms,
    IReadOnlyList<string> MatchedBodyTerms);
