namespace If.MicrosoftGraph;

/// <summary>Result of a delegated token acquisition, carrying token metadata and signed-in user identity.</summary>
public sealed record TokenAcquisitionResult(
    string                AccessToken,
    DateTimeOffset        ExpiresOn,
    IReadOnlyList<string> GrantedScopes,
    string                UserUpn,
    string                UserObjectId);
