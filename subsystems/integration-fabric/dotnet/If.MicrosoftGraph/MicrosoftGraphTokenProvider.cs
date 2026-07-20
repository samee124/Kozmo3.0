using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using Microsoft.Identity.Client;

namespace If.MicrosoftGraph;

/// <summary>
/// Acquires delegated access tokens for Microsoft Graph via OAuth2 auth-code + PKCE flow,
/// using a confidential client with a local HTTP redirect listener.
/// </summary>
public sealed class MicrosoftGraphTokenProvider
{
    private readonly IConfidentialClientApplication       _app;
    private readonly IReadOnlyList<string>                _scopes;
    private readonly string                               _redirectUri;
    private readonly ConcurrentDictionary<string, IAccount> _accountCache = new();

    /// <summary>Builds a MSAL confidential-client application from the supplied options.</summary>
    public MicrosoftGraphTokenProvider(MicrosoftGraphOptions options)
    {
        _app = ConfidentialClientApplicationBuilder
            .Create(options.ClientId)
            .WithClientSecret(options.ClientSecret)
            .WithAuthority(AzureCloudInstance.AzurePublic, options.TenantId)
            .WithRedirectUri(options.RedirectUri)
            .Build();

        _scopes      = options.Scopes;
        _redirectUri = options.RedirectUri;

        if (!string.IsNullOrWhiteSpace(options.TokenCachePath))
            PersistentTokenCache.Attach(_app, options.TokenCachePath);
    }

    /// <summary>
    /// Opens the system browser for interactive sign-in. Starts a local HTTP listener
    /// on the redirect URI, receives the auth code, and exchanges it for tokens.
    /// </summary>
    public async Task<TokenAcquisitionResult> AcquireInteractiveAsync(CancellationToken ct = default)
    {
        // Build auth URL
        var authUrl = await _app
            .GetAuthorizationRequestUrl(_scopes)
            .WithPrompt(Prompt.SelectAccount)
            .ExecuteAsync(ct);

        // Start local listener before opening browser so the redirect is caught
        var listenerPrefix = BuildListenerPrefix(_redirectUri);
        using var listener = new HttpListener();
        listener.Prefixes.Add(listenerPrefix);
        listener.Start();

        // Open browser
        Process.Start(new ProcessStartInfo(authUrl.ToString()) { UseShellExecute = true });
        Console.WriteLine("Browser opened. Waiting for sign-in redirect...");

        // Wait for the auth code on the redirect URI
        var context = await listener.GetContextAsync().WaitAsync(ct);
        var code    = context.Request.QueryString["code"];
        var error   = context.Request.QueryString["error"];

        // Send a completion page back to the browser
        var html  = error is null
            ? "<html><body style='font-family:sans-serif'><h2>Sign-in complete — you may close this tab.</h2></body></html>"
            : $"<html><body style='font-family:sans-serif'><h2>Sign-in failed: {error}</h2></body></html>";
        var bytes = System.Text.Encoding.UTF8.GetBytes(html);
        context.Response.ContentType     = "text/html; charset=utf-8";
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes, ct);
        context.Response.Close();

        if (error is not null || string.IsNullOrWhiteSpace(code))
            throw new InvalidOperationException(
                $"Auth redirect returned an error: {error ?? "no code received"}");

        // Exchange auth code for tokens
        var result = await _app
            .AcquireTokenByAuthorizationCode(_scopes, code)
            .ExecuteAsync(ct);

        // Cache the account so AcquireSilentAsync can find it without GetAccountsAsync
        _accountCache[result.Account.HomeAccountId.ObjectId] = result.Account;

        return ToResult(result);
    }

    /// <summary>
    /// Attempts silent token acquisition for ANY cached account (first one found).
    /// Returns null when the persistent cache is empty or the session has expired.
    /// Use this from worker services that don't know the user object ID ahead of time —
    /// after the user signs in interactively via GraphAuthHarness the cache is populated.
    /// </summary>
    public async Task<TokenAcquisitionResult?> TryAcquireAnySilentAsync(CancellationToken ct = default)
    {
#pragma warning disable CS0618
        var allAccounts = await _app.GetAccountsAsync();
#pragma warning restore CS0618
        var account = allAccounts.FirstOrDefault();
        if (account is null) return null;

        try
        {
            var result = await _app.AcquireTokenSilent(_scopes, account).ExecuteAsync(ct);
            _accountCache[result.Account.HomeAccountId.ObjectId] = result.Account;
            return ToResult(result);
        }
        catch (MsalUiRequiredException)
        {
            return null;
        }
    }

    /// <summary>
    /// Attempts silent token acquisition for the given user object ID.
    /// Returns null (instead of throwing) when no cached session is found or the session has
    /// expired and re-authentication is required. Use this from worker services where interactive
    /// sign-in is not possible; the result being null means "skip this cycle".
    /// </summary>
    public async Task<TokenAcquisitionResult?> TryAcquireSilentAsync(
        string userObjectId, CancellationToken ct = default)
    {
        if (!_accountCache.TryGetValue(userObjectId, out var account))
        {
            // Check the persistent cache (populated on previous process run).
            // GetAccountsAsync is deprecated on IConfidentialClientApplication but remains
            // functional for delegated flows where user accounts are stored in UserTokenCache.
#pragma warning disable CS0618
            var allAccounts = await _app.GetAccountsAsync();
#pragma warning restore CS0618
            account = allAccounts.FirstOrDefault(
                a => a.HomeAccountId.ObjectId == userObjectId);
            if (account is not null)
                _accountCache[userObjectId] = account;
        }

        if (account is null) return null;

        try
        {
            var result = await _app.AcquireTokenSilent(_scopes, account).ExecuteAsync(ct);
            _accountCache[result.Account.HomeAccountId.ObjectId] = result.Account;
            return ToResult(result);
        }
        catch (MsalUiRequiredException)
        {
            // Session expired — interactive sign-in required
            return null;
        }
    }

    /// <summary>
    /// Acquires a token silently from the in-memory cache for the given user object ID.
    /// Uses the refresh token automatically when the access token is expired.
    /// Throws if no cached account is found — call AcquireInteractiveAsync first.
    /// </summary>
    public async Task<TokenAcquisitionResult> AcquireSilentAsync(string userObjectId, CancellationToken ct = default)
    {
        if (!_accountCache.TryGetValue(userObjectId, out var account))
            throw new InvalidOperationException(
                $"No cached account found for user object ID '{userObjectId}'. " +
                "Call AcquireInteractiveAsync first to populate the in-memory cache.");

        var result = await _app
            .AcquireTokenSilent(_scopes, account)
            .ExecuteAsync(ct);

        return ToResult(result);
    }

    /// <summary>
    /// Builds the Microsoft Entra authorization URL for a web browser redirect.
    /// Redirect the user's browser to the returned URL to start sign-in.
    /// </summary>
    public async Task<string> BuildWebAuthorizationUrlAsync(CancellationToken ct = default)
    {
        var uri = await _app
            .GetAuthorizationRequestUrl(_scopes)
            .WithPrompt(Prompt.SelectAccount)
            .ExecuteAsync(ct);
        return uri.ToString();
    }

    /// <summary>
    /// Exchanges an authorization code received at the web redirect URI for tokens.
    /// Used by the web callback endpoint after the browser returns from Entra.
    /// </summary>
    public async Task<TokenAcquisitionResult> AcquireByCodeAsync(string code, CancellationToken ct = default)
    {
        var result = await _app
            .AcquireTokenByAuthorizationCode(_scopes, code)
            .ExecuteAsync(ct);

        _accountCache[result.Account.HomeAccountId.ObjectId] = result.Account;
        return ToResult(result);
    }

    /// <summary>Returns the listener prefix (scheme + host + port + /) from the redirect URI.</summary>
    private static string BuildListenerPrefix(string redirectUri)
    {
        var uri = new Uri(redirectUri);
        return $"{uri.Scheme}://{uri.Host}:{uri.Port}/";
    }

    private static TokenAcquisitionResult ToResult(AuthenticationResult r) => new(
        AccessToken:   r.AccessToken,
        ExpiresOn:     r.ExpiresOn,
        GrantedScopes: r.Scopes.ToList(),
        UserUpn:       r.Account.Username,
        UserObjectId:  r.Account.HomeAccountId.ObjectId);
}
