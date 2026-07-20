namespace If.Contracts;

/// <summary>Parameters for narrowing mail retrieval to vendor-relevant messages.</summary>
public sealed record MailSearchCriteria(
    Guid                  VendorId,
    IReadOnlyList<string> VendorDomains,
    IReadOnlyList<string> MeetingParticipants,
    DateTimeOffset        FromUtc,
    DateTimeOffset        ToUtc,
    IReadOnlyList<string> CommercialTerms,
    int                   MaximumMessages);
