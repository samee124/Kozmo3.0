namespace If.Contracts;

/// <summary>A single mail message captured from a mail source.</summary>
public sealed record MailArtifact(
    Guid                  ArtifactId,
    string                SourceSystem,
    string                SourceType,
    string                TenantId,
    string                SourcePrincipalId,
    string                ExternalId,
    string                ConversationId,
    string                Subject,
    string                Sender,
    IReadOnlyList<string> Recipients,
    string                BodyPreview,
    DateTimeOffset        SentAtUtc,
    DateTimeOffset        CapturedAtUtc);
