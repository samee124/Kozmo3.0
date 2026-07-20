using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace Wc.CheckIn;

/// <summary>
/// Posts follow-up payloads to a Slack response_url.
/// Used by /slack/interactivity to update a button message after background processing.
/// HttpClient is owned here so Kozmo.Api (which bans HttpClient) never references it.
/// Swallows all exceptions — delivery failure must not surface to the caller.
/// </summary>
public sealed class SlackResponsePoster
{
    private readonly HttpClient _http = new();

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy         = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition       = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>
    /// POST <paramref name="payload"/> (serialised as JSON) to <paramref name="responseUrl"/>.
    /// Slack response_urls are pre-authenticated — no Authorization header needed.
    /// </summary>
    public async Task PostAsync(string responseUrl, object payload, CancellationToken ct = default)
    {
        try
        {
            var json = JsonSerializer.Serialize(payload, JsonOpts);
            using var req = new HttpRequestMessage(HttpMethod.Post, responseUrl);
            req.Content = new StringContent(json, Encoding.UTF8, "application/json");
            var resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
                Console.WriteLine($"[slack] response_url POST returned {(int)resp.StatusCode}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[slack] response_url POST exception: {ex.Message}");
        }
    }
}
