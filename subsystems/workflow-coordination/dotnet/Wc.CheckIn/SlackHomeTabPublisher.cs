using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Wc.CheckIn;

using CheckIn = global::Wc.Contracts.CheckIn;

/// <summary>
/// Publishes the Kozmo Home tab for a Slack user via the views.publish API.
/// HttpClient is created internally so Kozmo.Api never holds a reference to it
/// (Kozmo.Api's BannedSymbols.txt bans T:System.Net.Http.HttpClient outright).
/// On any transport failure: logs and returns — home tab is advisory, not critical.
/// </summary>
public sealed class SlackHomeTabPublisher
{
    private readonly HttpClient? _http;
    private readonly string?     _botToken;

    /// <summary>
    /// Production ctor — creates an internal HttpClient when the bot token is present.
    /// Pass null to produce a no-op publisher (Slack not configured).
    /// </summary>
    public SlackHomeTabPublisher(string? botToken)
    {
        if (botToken is not null)
        {
            _http     = new HttpClient();
            _botToken = botToken;
        }
    }

    /// <summary>
    /// Test ctor — inject a capturing HttpClient so no real Slack API is called.
    /// </summary>
    public SlackHomeTabPublisher(HttpClient http, string botToken)
    {
        _http     = http;
        _botToken = botToken;
    }

    /// <summary>
    /// Publish (or refresh) the Home tab view for <paramref name="slackUserId"/>.
    /// Lists <paramref name="openCheckIns"/> as Block Kit sections.
    /// Swallows all exceptions — view refresh on next open is the fallback.
    /// </summary>
    public async Task PublishAsync(
        string                  slackUserId,
        IReadOnlyList<CheckIn>  openCheckIns,
        CancellationToken       ct = default)
    {
        if (_http is null || _botToken is null) return;

        var blocks  = BuildHomeBlocks(openCheckIns);
        var payload = JsonSerializer.Serialize(new
        {
            user_id = slackUserId,
            view    = new { type = "home", blocks }
        });

        using var req = new HttpRequestMessage(HttpMethod.Post, "https://slack.com/api/views.publish");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _botToken);
        req.Content = new StringContent(payload, Encoding.UTF8, "application/json");

        try
        {
            var resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
                Console.WriteLine($"[slack] views.publish returned {(int)resp.StatusCode} for user {slackUserId}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[slack] views.publish exception for user {slackUserId}: {ex.Message}");
        }
    }

    // Internal so unit tests can assert on the generated block structure without going through HTTP.
    internal static object[] BuildHomeBlocks(IReadOnlyList<CheckIn> checkIns)
    {
        var blocks = new List<object>
        {
            new { type = "header", text = new { type = "plain_text", text = "Kozmo \u2014 Your Open Check-ins", emoji = true } }
        };

        if (checkIns.Count == 0)
        {
            blocks.Add(new { type = "section",
                text = new { type = "mrkdwn", text = "No open check-ins \u2014 all clear." } });
            return [.. blocks];
        }

        blocks.Add(new { type = "divider" });

        foreach (var ci in checkIns)
        {
            blocks.Add(new
            {
                type = "section",
                text = new
                {
                    type = "mrkdwn",
                    text = $"*{EscapeMrkdwn(ci.Question)}*\n_Raised:_ {ci.RaisedAt:MMM d, yyyy}"
                }
            });
        }

        return [.. blocks];
    }

    private static string EscapeMrkdwn(string s) =>
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}
