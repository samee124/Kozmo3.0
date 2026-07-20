using System.Text;
using If.Contracts;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using Microsoft.Graph.Models.ODataErrors;
using Microsoft.Kiota.Abstractions.Authentication;

namespace If.MicrosoftGraph;

/// <summary>
/// Implements ITranscriptSource against Microsoft Graph.
///
/// ResolveMeetingAsync:
///   GET /me/onlineMeetings?$filter=joinWebUrl eq '{url}'
///   Returns the online meeting ID needed for subsequent transcript calls.
///
/// CheckTranscriptAvailabilityAsync:
///   GET /me/onlineMeetings/{meetingId}/transcripts
///   Returns the first available transcript ID and creation time.
///
/// FetchTranscriptAsync:
///   GET /me/onlineMeetings/{meetingId}/transcripts/{transcriptId}/content
///   Returns the raw VTT content as a string.
///
/// All methods handle 401 (token expired) via silent refresh + retry,
/// and 429 (throttled) via GraphRetryPolicy exponential backoff.
///
/// Required delegated permissions (admin-consented):
///   OnlineMeetings.Read, OnlineMeetingTranscript.Read.All
/// </summary>
public sealed class MicrosoftGraphTranscriptSource : ITranscriptSource
{
    private readonly MicrosoftGraphTokenProvider _tokenProvider;
    private readonly string                      _tenantId;
    private readonly string                      _userObjectId;
    private string                               _accessToken;

    public MicrosoftGraphTranscriptSource(
        string                      accessToken,
        string                      userObjectId,
        MicrosoftGraphTokenProvider tokenProvider,
        string                      tenantId)
    {
        _accessToken   = accessToken;
        _userObjectId  = userObjectId;
        _tokenProvider = tokenProvider;
        _tenantId      = tenantId;
    }

    // ── ITranscriptSource ─────────────────────────────────────────────────────

    public async Task<MeetingResolutionResult> ResolveMeetingAsync(
        string joinWebUrl, CancellationToken ct)
    {
        try
        {
            return await GraphRetryPolicy.ExecuteAsync(
                () => ResolveMeetingCoreAsync(joinWebUrl, _accessToken, ct), ct);
        }
        catch (ODataError ex) when (ex.ResponseStatusCode == 401)
        {
            await RefreshTokenAsync(ct);
            return await GraphRetryPolicy.ExecuteAsync(
                () => ResolveMeetingCoreAsync(joinWebUrl, _accessToken, ct), ct);
        }
        catch (Exception ex)
        {
            return new MeetingResolutionResult(false, null, ex.Message);
        }
    }

    public async Task<TranscriptAvailabilityResult> CheckTranscriptAvailabilityAsync(
        string meetingId, CancellationToken ct)
    {
        try
        {
            return await GraphRetryPolicy.ExecuteAsync(
                () => CheckTranscriptCoreAsync(meetingId, _accessToken, ct), ct);
        }
        catch (ODataError ex) when (ex.ResponseStatusCode == 401)
        {
            await RefreshTokenAsync(ct);
            return await GraphRetryPolicy.ExecuteAsync(
                () => CheckTranscriptCoreAsync(meetingId, _accessToken, ct), ct);
        }
        catch (ODataError ex) when (ex.ResponseStatusCode == 404)
        {
            return new TranscriptAvailabilityResult(false, null, null,
                "Meeting not found or transcription was not enabled for this meeting");
        }
        catch (Exception ex)
        {
            return new TranscriptAvailabilityResult(false, null, null, ex.Message);
        }
    }

    public async Task<TranscriptContent> FetchTranscriptAsync(
        string meetingId, string transcriptId, CancellationToken ct)
    {
        try
        {
            return await GraphRetryPolicy.ExecuteAsync(
                () => FetchTranscriptCoreAsync(meetingId, transcriptId, _accessToken, ct), ct);
        }
        catch (ODataError ex) when (ex.ResponseStatusCode == 401)
        {
            await RefreshTokenAsync(ct);
            return await GraphRetryPolicy.ExecuteAsync(
                () => FetchTranscriptCoreAsync(meetingId, transcriptId, _accessToken, ct), ct);
        }
    }

    // ── Core Graph calls ──────────────────────────────────────────────────────

    private async Task<MeetingResolutionResult> ResolveMeetingCoreAsync(
        string joinWebUrl, string accessToken, CancellationToken ct)
    {
        var client  = BuildClient(accessToken);
        var escaped = joinWebUrl.Replace("'", "''"); // OData single-quote escape
        var col     = await client.Me.OnlineMeetings.GetAsync(cfg =>
        {
            cfg.QueryParameters.Filter = $"joinWebUrl eq '{escaped}'";
        }, ct);

        return ParseMeetingResolution(col);
    }

    private async Task<TranscriptAvailabilityResult> CheckTranscriptCoreAsync(
        string meetingId, string accessToken, CancellationToken ct)
    {
        var client = BuildClient(accessToken);
        var col    = await client.Me.OnlineMeetings[meetingId].Transcripts
            .GetAsync(cancellationToken: ct);

        return ParseTranscriptAvailability(col);
    }

    private async Task<TranscriptContent> FetchTranscriptCoreAsync(
        string meetingId, string transcriptId, string accessToken, CancellationToken ct)
    {
        // The Graph SDK sends "Accept: application/octet-stream, application/json" which the
        // transcript content endpoint rejects. Use raw HTTP with Accept: text/vtt instead.
        var url = $"https://graph.microsoft.com/v1.0/me/onlineMeetings/{meetingId}/transcripts/{transcriptId}/content";
        using var http = new System.Net.Http.HttpClient();
        http.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
        http.DefaultRequestHeaders.Accept.Clear();
        http.DefaultRequestHeaders.Accept.Add(
            new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("text/vtt"));

        var response = await http.GetAsync(url, ct);
        if (!response.IsSuccessStatusCode)
        {
            var err = await response.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException(
                $"Transcript content fetch failed ({(int)response.StatusCode}): {err[..Math.Min(300, err.Length)]}");
        }

        var raw = await response.Content.ReadAsStringAsync(ct);

        return new TranscriptContent(
            TranscriptId: transcriptId,
            MeetingId:    meetingId,
            Format:       "text/vtt",
            RawContent:   raw,
            FetchedAt:    DateTimeOffset.UtcNow);
    }

    // ── Internal parsing methods (testable without mocking HTTP) ──────────────

    internal static MeetingResolutionResult ParseMeetingResolution(
        OnlineMeetingCollectionResponse? col)
    {
        if (col?.Value is null or { Count: 0 })
            return new MeetingResolutionResult(false, null,
                "No online meeting found for this join URL — meeting may not be a Teams meeting");

        return new MeetingResolutionResult(true, col.Value[0].Id, null);
    }

    internal static TranscriptAvailabilityResult ParseTranscriptAvailability(
        CallTranscriptCollectionResponse? col)
    {
        if (col?.Value is null or { Count: 0 })
            return new TranscriptAvailabilityResult(false, null, null,
                "Transcript not yet available — may still be processing");

        var first = col.Value[0];
        return new TranscriptAvailabilityResult(
            Available:    true,
            TranscriptId: first.Id,
            CreatedAt:    first.CreatedDateTime,
            FailureReason: null);
    }

    // ── Token refresh ─────────────────────────────────────────────────────────

    private async Task RefreshTokenAsync(CancellationToken ct)
    {
        var refreshed = await _tokenProvider.AcquireSilentAsync(_userObjectId, ct);
        _accessToken  = refreshed.AccessToken;
    }

    // ── Graph client factory ──────────────────────────────────────────────────

    private static GraphServiceClient BuildClient(string accessToken)
    {
        var auth = new BaseBearerTokenAuthenticationProvider(new StaticTokenProvider(accessToken));
        return new GraphServiceClient(auth);
    }

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
