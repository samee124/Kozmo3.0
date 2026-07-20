namespace If.Contracts;

/// <summary>A single calendar event captured from a calendar source.</summary>
public sealed record CalendarArtifact(
    Guid                  ArtifactId,
    string                SourceSystem,
    string                SourceType,
    string                TenantId,
    string                SourcePrincipalId,
    string                ExternalId,
    string                ICalUid,
    string                Subject,
    DateTimeOffset        StartUtc,
    DateTimeOffset        EndUtc,
    string                Organizer,
    IReadOnlyList<string> Attendees,
    string                BodyPreview,
    DateTimeOffset        CapturedAtUtc,
    string?               JoinWebUrl = null);
