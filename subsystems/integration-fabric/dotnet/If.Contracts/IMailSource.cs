namespace If.Contracts;

/// <summary>Finds mail messages relevant to a vendor according to supplied criteria.</summary>
public interface IMailSource
{
    Task<IReadOnlyList<MailArtifact>> FindRelevantMessagesAsync(
        string signedInUserPrincipalId,
        MailSearchCriteria criteria,
        CancellationToken ct);
}
