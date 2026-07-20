namespace Po.VendorCall;

public enum TranscriptItemType
{
    Decision,
    Commitment,
    PricingSignal,
    ServiceSignal,
    OpenQuestion,
    NextStep
}

/// <summary>A single commercial item extracted from a meeting transcript.</summary>
public sealed record TranscriptExtractedItem(
    TranscriptItemType Type,
    string             Description,
    string             Speaker,
    string?            CounterParty,
    string             Quote,
    TimeSpan           TranscriptTimestamp,
    double             Confidence,
    string             ClaimKey,
    string?            Owner,
    string?            DueDate,
    bool               RequiresUserConfirmation);

/// <summary>Full result of running transcript comprehension over one meeting.</summary>
public sealed record TranscriptExtractionResult(
    IReadOnlyList<TranscriptExtractedItem> Items,
    IReadOnlyList<PreBriefItemResolution>  ResolvedPreBriefItems,
    TranscriptExtractionMetadata           Metadata);

/// <summary>Resolution status of one open item from the pre-meeting brief.</summary>
public sealed record PreBriefItemResolution(
    string    PreBriefItem,
    bool      AddressedInMeeting,
    string?   TranscriptEvidence,
    TimeSpan? TranscriptTimestamp,
    double    Confidence);

/// <summary>Extraction run statistics.</summary>
public sealed record TranscriptExtractionMetadata(
    int      TotalSegmentsProcessed,
    int      TotalItemsExtracted,
    int      HighConfidenceCount,
    int      RequiresConfirmationCount,
    int      DiscardedLowConfidenceCount,
    TimeSpan ProcessingDuration);
