using If.Contracts;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using Microsoft.Graph.Models.ODataErrors;
using Microsoft.Kiota.Abstractions.Authentication;

namespace If.MicrosoftGraph;

/// <summary>Microsoft Graph adapter for mail message retrieval via delegated auth.</summary>
public sealed class MicrosoftGraphMailSource : IMailSource
{
    private readonly MicrosoftGraphTokenProvider _tokenProvider;
    private readonly string                      _tenantId;
    private readonly string                      _userObjectId;
    private string                               _accessToken;

    /// <summary>Creates a mail source bound to the given user session and token.</summary>
    public MicrosoftGraphMailSource(
        string accessToken,
        string userObjectId,
        MicrosoftGraphTokenProvider tokenProvider,
        string tenantId)
    {
        _accessToken   = accessToken;
        _userObjectId  = userObjectId;
        _tokenProvider = tokenProvider;
        _tenantId      = tenantId;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<MailArtifact>> FindRelevantMessagesAsync(
        string signedInUserPrincipalId,
        MailSearchCriteria criteria,
        CancellationToken ct)
    {
        try
        {
            return await GraphRetryPolicy.ExecuteAsync(
                () => FetchMessagesAsync(signedInUserPrincipalId, criteria, _accessToken, ct), ct);
        }
        catch (ODataError ex) when (ex.ResponseStatusCode == 401)
        {
            TokenAcquisitionResult refreshed;
            try
            {
                refreshed = await _tokenProvider.AcquireSilentAsync(_userObjectId, ct);
            }
            catch (Exception refreshEx)
            {
                throw new InvalidOperationException(
                    "Token expired and silent refresh failed — user needs to sign in again.",
                    refreshEx);
            }
            _accessToken = refreshed.AccessToken;
            return await GraphRetryPolicy.ExecuteAsync(
                () => FetchMessagesAsync(signedInUserPrincipalId, criteria, _accessToken, ct), ct);
        }
    }

    private async Task<IReadOnlyList<MailArtifact>> FetchMessagesAsync(
        string signedInUserPrincipalId,
        MailSearchCriteria criteria,
        string accessToken,
        CancellationToken ct)
    {
        var client  = BuildClient(accessToken);
        var results = new List<MailArtifact>();
        var cap     = Math.Max(1, Math.Min(criteria.MaximumMessages, 50));

        // Graph $filter on receivedDateTime; domain filtering is done client-side
        var from   = criteria.FromUtc.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ");
        var to     = criteria.ToUtc.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ");
        var filter = $"receivedDateTime ge {from} and receivedDateTime le {to}";

        var page = await client.Me.Messages.GetAsync(cfg =>
        {
            cfg.QueryParameters.Filter  = filter;
            cfg.QueryParameters.Orderby = ["receivedDateTime desc"];
            cfg.QueryParameters.Top     = 50; // fetch max per page; cap applied after domain filter
            cfg.QueryParameters.Select  =
                ["id", "conversationId", "subject", "from",
                 "toRecipients", "ccRecipients", "bodyPreview",
                 "sentDateTime", "receivedDateTime"];
        }, ct);

        while (page is not null && results.Count < cap)
        {
            foreach (var msg in page.Value ?? [])
            {
                if (msg is null) continue;
                if (!MatchesDomain(msg, criteria.VendorDomains)) continue;
                results.Add(GraphMailMapper.Map(msg, _tenantId, signedInUserPrincipalId));
                if (results.Count >= cap) break;
            }

            if (page.OdataNextLink is null || results.Count >= cap) break;

            page = await client.Me.Messages
                .WithUrl(page.OdataNextLink)
                .GetAsync(cancellationToken: ct);
        }

        return results;
    }

    /// <summary>
    /// Returns true when the message sender or any recipient matches a vendor domain.
    /// When <paramref name="domains"/> is empty, all messages are considered relevant.
    /// </summary>
    private static bool MatchesDomain(Message msg, IReadOnlyList<string> domains)
    {
        if (domains.Count == 0) return true;

        var senderDomain = ExtractDomain(msg.From?.EmailAddress?.Address);
        if (senderDomain is not null &&
            domains.Any(d => d.Equals(senderDomain, StringComparison.OrdinalIgnoreCase)))
            return true;

        var allRecipients = (msg.ToRecipients ?? []).Concat(msg.CcRecipients ?? []);
        foreach (var r in allRecipients)
        {
            var domain = ExtractDomain(r.EmailAddress?.Address);
            if (domain is not null &&
                domains.Any(d => d.Equals(domain, StringComparison.OrdinalIgnoreCase)))
                return true;
        }
        return false;
    }

    private static string? ExtractDomain(string? email)
    {
        if (string.IsNullOrEmpty(email)) return null;
        var at = email.IndexOf('@');
        return at >= 0 ? email[(at + 1)..] : null;
    }

    private static GraphServiceClient BuildClient(string accessToken)
    {
        var auth = new BaseBearerTokenAuthenticationProvider(new StaticTokenProvider(accessToken));
        return new GraphServiceClient(auth);
    }

    /// <summary>Supplies a static bearer token to the Graph SDK's authentication pipeline.</summary>
    private sealed class StaticTokenProvider : IAccessTokenProvider
    {
        private readonly string _token;
        public StaticTokenProvider(string token) => _token = token;

        public Task<string> GetAuthorizationTokenAsync(
            Uri uri,
            Dictionary<string, object>? additionalAuthenticationContext = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(_token);

        public AllowedHostsValidator AllowedHostsValidator { get; } =
            new(["graph.microsoft.com"]);
    }
}
