using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using If.Contracts;

namespace Po.VendorCall;

/// <summary>
/// IMailSource implementation that loads from a pre-curated JSON fixture file
/// (e.g. tools/GraphAuthHarness/fixtures/northstar_emails.json).
///
/// Time-window filtering is intentionally skipped — fixture emails are pre-selected
/// as relevant regardless of date. Domain matching is applied to keep the same
/// filter logic as live sources.
/// </summary>
public sealed class FixtureMailSource : IMailSource
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private readonly string _fixturePath;

    public FixtureMailSource(string fixturePath)
        => _fixturePath = fixturePath;

    public async Task<IReadOnlyList<MailArtifact>> FindRelevantMessagesAsync(
        string            signedInUserPrincipalId,
        MailSearchCriteria criteria,
        CancellationToken  ct)
    {
        var json  = await File.ReadAllTextAsync(_fixturePath, ct);
        var items = JsonSerializer.Deserialize<List<FixtureEmailItem>>(json, JsonOpts) ?? [];

        return items
            .Where(item => MatchesDomains(item.Sender, item.Recipients, criteria.VendorDomains))
            .Take(criteria.MaximumMessages)
            .Select(item => new MailArtifact(
                ArtifactId:        StableGuid(item.MessageId),
                SourceSystem:      "fixture",
                SourceType:        "fixture_email",
                TenantId:          "fixture",
                SourcePrincipalId: signedInUserPrincipalId,
                ExternalId:        item.MessageId,
                ConversationId:    item.ConversationId,
                Subject:           item.Subject,
                Sender:            item.Sender,
                Recipients:        item.Recipients,
                BodyPreview:       item.BodyPreview,
                SentAtUtc:         item.SentAtUtc,
                CapturedAtUtc:     item.SentAtUtc))   // use SentAtUtc to stay deterministic
            .ToList();
    }

    private static bool MatchesDomains(string sender, List<string> recipients, IReadOnlyList<string> domains)
    {
        if (domains.Count == 0) return true;
        return domains.Any(d =>
            sender.EndsWith($"@{d}", StringComparison.OrdinalIgnoreCase) ||
            recipients.Any(r => r.EndsWith($"@{d}", StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>Derives a deterministic Guid from the message_id string.</summary>
    private static Guid StableGuid(string input)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return new Guid(hash[..16]);
    }
}

/// <summary>Private DTO matching the fixture JSON schema.</summary>
file sealed record FixtureEmailItem(
    [property: JsonPropertyName("message_id")]      string MessageId,
    [property: JsonPropertyName("conversation_id")] string ConversationId,
    [property: JsonPropertyName("subject")]         string Subject,
    [property: JsonPropertyName("sender")]          string Sender,
    [property: JsonPropertyName("recipients")]      List<string> Recipients,
    [property: JsonPropertyName("sent_at_utc")]     DateTimeOffset SentAtUtc,
    [property: JsonPropertyName("body_preview")]    string BodyPreview);
