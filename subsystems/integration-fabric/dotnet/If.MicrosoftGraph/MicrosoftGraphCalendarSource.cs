using If.Contracts;
using Microsoft.Graph;
using Microsoft.Graph.Models.ODataErrors;
using Microsoft.Kiota.Abstractions.Authentication;

namespace If.MicrosoftGraph;

/// <summary>Microsoft Graph adapter for calendar event retrieval via delegated auth.</summary>
public sealed class MicrosoftGraphCalendarSource : ICalendarSource
{
    private readonly MicrosoftGraphTokenProvider _tokenProvider;
    private readonly string                      _tenantId;
    private readonly string                      _userObjectId;
    private string                               _accessToken;

    /// <summary>Creates a calendar source bound to the given user session and token.</summary>
    public MicrosoftGraphCalendarSource(
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
    public async Task<IReadOnlyList<CalendarArtifact>> GetEventsAsync(
        string signedInUserPrincipalId,
        CalendarWindow window,
        CancellationToken ct)
    {
        try
        {
            return await GraphRetryPolicy.ExecuteAsync(
                () => FetchEventsAsync(signedInUserPrincipalId, window, _accessToken, ct), ct);
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
                () => FetchEventsAsync(signedInUserPrincipalId, window, _accessToken, ct), ct);
        }
    }

    private async Task<IReadOnlyList<CalendarArtifact>> FetchEventsAsync(
        string signedInUserPrincipalId,
        CalendarWindow window,
        string accessToken,
        CancellationToken ct)
    {
        var client  = BuildClient(accessToken);
        var results = new List<CalendarArtifact>();

        var page = await client.Me.CalendarView.GetAsync(cfg =>
        {
            cfg.QueryParameters.StartDateTime = window.FromUtc.ToString("o");
            cfg.QueryParameters.EndDateTime   = window.ToUtc.ToString("o");
            cfg.QueryParameters.Select        =
                ["id", "iCalUId", "subject", "start", "end",
                 "organizer", "attendees", "bodyPreview", "onlineMeeting"];
            cfg.Headers.Add("Prefer", "outlook.timezone=\"UTC\"");
        }, ct);

        while (page is not null)
        {
            foreach (var ev in page.Value ?? [])
                if (ev is not null)
                    results.Add(GraphCalendarMapper.Map(ev, _tenantId, signedInUserPrincipalId));

            if (page.OdataNextLink is null) break;

            page = await client.Me.CalendarView
                .WithUrl(page.OdataNextLink)
                .GetAsync(cfg => cfg.Headers.Add("Prefer", "outlook.timezone=\"UTC\""), ct);
        }

        return results;
    }

    /// <summary>
    /// Returns true when the calendar event has been cancelled by the organizer or deleted entirely.
    /// Returns false on any transient failure (safe default — do not cancel runs on network glitches).
    /// </summary>
    public async Task<bool> IsEventCancelledOrDeletedAsync(string rawGraphEventId, CancellationToken ct)
    {
        try
        {
            return await GraphRetryPolicy.ExecuteAsync(
                () => CheckCancelledCoreAsync(rawGraphEventId, _accessToken, ct), ct);
        }
        catch (ODataError ex) when (ex.ResponseStatusCode == 401)
        {
            try
            {
                var refreshed = await _tokenProvider.AcquireSilentAsync(_userObjectId, ct);
                _accessToken = refreshed.AccessToken;
                return await GraphRetryPolicy.ExecuteAsync(
                    () => CheckCancelledCoreAsync(rawGraphEventId, _accessToken, ct), ct);
            }
            catch
            {
                return false;
            }
        }
        catch
        {
            return false; // Transient failure — do not cancel the run
        }
    }

    private static async Task<bool> CheckCancelledCoreAsync(
        string rawEventId, string accessToken, CancellationToken ct)
    {
        try
        {
            var client = BuildClient(accessToken);
            var ev = await client.Me.Events[rawEventId].GetAsync(cfg =>
            {
                cfg.QueryParameters.Select = ["id", "isCancelled"];
            }, ct);
            return ev?.IsCancelled == true;
        }
        catch (ODataError ex) when (ex.ResponseStatusCode == 404)
        {
            return true; // Event deleted from organizer's calendar
        }
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
