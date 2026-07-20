using If.Contracts;
using Microsoft.Graph.Models;

namespace If.MicrosoftGraph;

/// <summary>Maps Microsoft Graph mail message responses to MailArtifact records.</summary>
public sealed class GraphMailMapper
{
    private const int MaxBodyPreviewLength = 500;

    /// <summary>Maps a Graph Message to a MailArtifact. Never throws on null Graph fields.</summary>
    public static MailArtifact Map(
        Message msg,
        string tenantId,
        string signedInUserPrincipalId)
        => new(
            ArtifactId:          Guid.NewGuid(),
            SourceSystem:        "microsoft_graph",
            SourceType:          "mail_message",
            TenantId:            tenantId,
            SourcePrincipalId:   signedInUserPrincipalId,
            ExternalId:          $"msgraph:message:{msg.Id ?? ""}",
            ConversationId:      msg.ConversationId ?? "",
            Subject:             msg.Subject ?? "",
            Sender:              msg.From?.EmailAddress?.Address ?? "",
            Recipients:          MapRecipients(msg.ToRecipients, msg.CcRecipients),
            BodyPreview:         Truncate(msg.BodyPreview ?? "", MaxBodyPreviewLength),
            SentAtUtc:           msg.SentDateTime ?? DateTimeOffset.UtcNow,
            CapturedAtUtc:       DateTimeOffset.UtcNow);

    private static IReadOnlyList<string> MapRecipients(
        List<Recipient>? toRecipients,
        List<Recipient>? ccRecipients)
    {
        var all = (toRecipients ?? []).Concat(ccRecipients ?? []);
        return all
            .Select(r => r.EmailAddress?.Address)
            .Where(addr => !string.IsNullOrEmpty(addr))
            .Select(addr => addr!)
            .ToList();
    }

    private static string Truncate(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength];
}
