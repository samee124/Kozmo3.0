using Google.Apis.Auth;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Flows;
using Google.Apis.Auth.OAuth2.Responses;
using Google.Apis.Drive.v3;

namespace Kozmo.Connector.GoogleDrive;

/// <summary>
/// Handles Google OAuth2 Authorization Code flow.
/// Builds the consent URL, exchanges the authorization code for tokens,
/// and refreshes expired tokens via the stored refresh token.
/// Does NOT persist tokens — caller stores the returned OAuthToken in SQLite.
/// </summary>
public sealed class GoogleOAuthService
{
    private readonly string _clientId;
    private readonly string _clientSecret;
    private readonly string _redirectUri;

    private static readonly string[] Scopes =
    [
        DriveService.Scope.DriveReadonly,
        "openid",
        "email",
        "profile"
    ];

    public GoogleOAuthService(string clientId, string clientSecret, string redirectUri)
    {
        _clientId     = clientId;
        _clientSecret = clientSecret;
        _redirectUri  = redirectUri;
    }

    /// <summary>True when GOOGLE_CLIENT_ID and GOOGLE_CLIENT_SECRET are both set.</summary>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(_clientId) && !string.IsNullOrWhiteSpace(_clientSecret);

    /// <summary>
    /// Builds the Google consent page URL. Redirect the browser here.
    /// access_type=offline ensures a refresh token is returned.
    /// prompt=consent forces the consent screen even for already-authorized accounts.
    /// </summary>
    public string BuildAuthorizationUrl()
    {
        var scope = Uri.EscapeDataString(string.Join(" ", Scopes));
        return "https://accounts.google.com/o/oauth2/v2/auth" +
               $"?client_id={Uri.EscapeDataString(_clientId)}" +
               $"&redirect_uri={Uri.EscapeDataString(_redirectUri)}" +
               $"&response_type=code" +
               $"&scope={scope}" +
               $"&access_type=offline" +
               $"&prompt=consent";
    }

    /// <summary>
    /// Exchanges the one-time authorization code (from the callback query string)
    /// for an access token + refresh token.
    /// </summary>
    public async Task<OAuthToken> ExchangeCodeAsync(string code, CancellationToken ct = default)
    {
        var flow     = CreateFlow();
        var response = await flow.ExchangeCodeForTokenAsync("user", code, _redirectUri, ct);
        return await ToOAuthTokenAsync(response);
    }

    /// <summary>
    /// Uses the stored refresh token to obtain a new access token.
    /// Returns a new OAuthToken with the same refresh token (Google does not rotate it on every refresh).
    /// </summary>
    public async Task<OAuthToken> RefreshAsync(string refreshToken, CancellationToken ct = default)
    {
        var flow     = CreateFlow();
        var response = await flow.RefreshTokenAsync("user", refreshToken, ct);
        var token    = await ToOAuthTokenAsync(response);
        // RefreshTokenAsync may not return a new refresh token — keep the existing one
        return token with { RefreshToken = string.IsNullOrEmpty(token.RefreshToken) ? refreshToken : token.RefreshToken };
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private GoogleAuthorizationCodeFlow CreateFlow() =>
        new(new GoogleAuthorizationCodeFlow.Initializer
        {
            ClientSecrets = new ClientSecrets { ClientId = _clientId, ClientSecret = _clientSecret },
            Scopes        = Scopes
        });

    private static async Task<OAuthToken> ToOAuthTokenAsync(TokenResponse response)
    {
        var expiresAt = DateTimeOffset.UtcNow.AddSeconds(response.ExpiresInSeconds ?? 3600);

        // Decode user email from the id_token JWT when present
        var email = "";
        if (!string.IsNullOrEmpty(response.IdToken))
        {
            try
            {
                var payload = await GoogleJsonWebSignature.ValidateAsync(response.IdToken);
                email = payload.Email ?? "";
            }
            catch { /* id_token absent or validation failed — email stays empty */ }
        }

        return new OAuthToken(
            AccessToken:  response.AccessToken  ?? "",
            RefreshToken: response.RefreshToken ?? "",
            ExpiresAt:    expiresAt,
            UserEmail:    email);
    }
}
