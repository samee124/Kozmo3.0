using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Wc.Contracts;

namespace Wc.CheckIn;

using CheckIn = global::Wc.Contracts.CheckIn;

/// <summary>
/// Slack transport for check-ins. Posts one Block Kit message per check-in
/// so that answering one (replace_original) never removes the others.
///
/// YES_NO questions: three action buttons (Yes / No / Unsure).
///   Each button's value carries a small JSON payload {checkInId, answer} so the
///   POST /slack/interactivity handler knows what was clicked.
///
/// TYPED_VALUE / STATUS_SELECT: an "Answer in Kozmo →" link button pointing to the
///   pending queue (no in-Slack typed input in this phase).
///
/// On any Slack API failure: logs to console and returns without throwing.
/// A delivery failure must not drop the check-in or crash the raise loop.
/// </summary>
public sealed class SlackCheckInTransport : ICheckInTransport
{
    private readonly HttpClient          _http;
    private readonly string              _botToken;
    private readonly string              _destination;
    private readonly Func<Guid, string?> _vendorName;

    private const string PostMessageUrl = "https://slack.com/api/chat.postMessage";

    public SlackCheckInTransport(
        HttpClient           http,
        string               botToken,
        string               destination,
        Func<Guid, string?>? vendorNameResolver = null)
    {
        _http        = http;
        _botToken    = botToken;
        _destination = destination;
        _vendorName  = vendorNameResolver ?? (_ => null);
    }

    public async Task SendAsync(IReadOnlyList<CheckIn> checkIns, CancellationToken ct = default)
    {
        if (checkIns.Count == 0) return;

        var uiBaseUrl  = Environment.GetEnvironmentVariable("KOZMO_UI_BASE_URL") ?? "http://localhost:3000";
        var vendorName = _vendorName(checkIns[0].VendorId) ?? "your vendor";

        // Send one message per check-in so that answering one does not replace the others.
        foreach (var ci in checkIns)
            await SendOneAsync(ci, vendorName, uiBaseUrl, ct);
    }

    private async Task SendOneAsync(CheckIn ci, string vendorName, string uiBaseUrl, CancellationToken ct)
    {
        var blocks = new List<object>();

        // Friendly intro
        blocks.Add(new
        {
            type = "section",
            text = new
            {
                type = "mrkdwn",
                text = $"Hi! I have a question about *{vendorName}*. Please respond when you can:"
            }
        });

        blocks.Add(new { type = "divider" });

        // Question
        blocks.Add(new
        {
            type = "section",
            text = new { type = "mrkdwn", text = $"*{ci.Question}*" }
        });

        if (ci.ResponseShape == ResponseShape.YES_NO)
        {
            blocks.Add(new
            {
                type     = "actions",
                elements = new object[]
                {
                    MakeButton("Yes",    "yes_btn",    ci, "true"),
                    MakeButton("No",     "no_btn",     ci, "false"),
                    MakeButton("Unsure", "unsure_btn", ci, "UNKNOWN")
                }
            });
        }
        else
        {
            var pendingUrl = $"{uiBaseUrl}/pending?highlight={ci.CheckInId}";
            blocks.Add(new
            {
                type     = "actions",
                elements = new object[]
                {
                    new
                    {
                        type      = "button",
                        text      = new { type = "plain_text", text = "Answer in Kozmo \u2192", emoji = true },
                        action_id = "open_kozmo",
                        url       = pendingUrl
                    }
                }
            });
        }

        var payload = new { channel = _destination, blocks };

        try
        {
            var json = JsonSerializer.Serialize(payload, JsonOpts);
            using var req = new HttpRequestMessage(HttpMethod.Post, PostMessageUrl);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _botToken);
            req.Content = new StringContent(json, Encoding.UTF8, "application/json");

            var resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync(ct);
                Console.WriteLine($"[slack-transport] chat.postMessage HTTP {(int)resp.StatusCode}: {body}");
            }
            else
            {
                var body = await resp.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("ok", out var ok) && !ok.GetBoolean())
                    Console.WriteLine($"[slack-transport] chat.postMessage not ok: {body}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[slack-transport] chat.postMessage exception for {ci.CheckInId}: {ex.Message}");
        }
    }

    private static object MakeButton(string label, string actionId, CheckIn ci, string answer)
    {
        var value = JsonSerializer.Serialize(
            new { checkInId = ci.CheckInId.ToString(), answer },
            JsonOpts);
        return new
        {
            type      = "button",
            text      = new { type = "plain_text", text = label },
            action_id = actionId,
            value
        };
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
}
