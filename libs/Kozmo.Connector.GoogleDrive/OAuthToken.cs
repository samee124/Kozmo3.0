namespace Kozmo.Connector.GoogleDrive;

/// <summary>OAuth2 token for a connected Google account. Persisted to SQLite.</summary>
public sealed record OAuthToken(
    string         AccessToken,
    string         RefreshToken,
    DateTimeOffset ExpiresAt,
    string         UserEmail
);
