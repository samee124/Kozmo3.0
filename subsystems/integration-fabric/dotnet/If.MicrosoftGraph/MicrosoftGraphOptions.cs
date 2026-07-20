namespace If.MicrosoftGraph;

/// <summary>Configuration for connecting to Microsoft Graph via delegated auth-code + PKCE flow.</summary>
public sealed record MicrosoftGraphOptions
{
    /// <summary>Entra tenant ID.</summary>
    public required string TenantId { get; init; }

    /// <summary>Entra application (client) ID.</summary>
    public required string ClientId { get; init; }

    /// <summary>
    /// Client secret — read from user secrets, never from source.
    /// Not consumed by the PKCE flow (public client); retained for future confidential-client use.
    /// </summary>
    public required string ClientSecret { get; init; }

    /// <summary>OAuth2 redirect URI registered under the mobile/desktop platform in Entra.</summary>
    public required string RedirectUri { get; init; }

    /// <summary>Delegated scopes to request during interactive sign-in.</summary>
    public required IReadOnlyList<string> Scopes { get; init; }

    /// <summary>
    /// Optional path to persist the MSAL token cache across restarts.
    /// Shared between GraphAuthHarness and Wj.MeetingPulse so the worker can use
    /// tokens acquired interactively. If null or empty, tokens are in-memory only.
    /// </summary>
    public string? TokenCachePath { get; init; }
}
