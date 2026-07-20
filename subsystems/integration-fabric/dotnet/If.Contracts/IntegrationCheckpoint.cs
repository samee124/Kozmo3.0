namespace If.Contracts;

/// <summary>Sync watermark for a given source system and principal.</summary>
public sealed record IntegrationCheckpoint(
    string         SourceSystem,
    string         PrincipalId,
    DateTimeOffset Watermark,
    DateTimeOffset LastRunUtc,
    string         Status);
