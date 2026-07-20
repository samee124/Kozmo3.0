namespace If.Contracts;

/// <summary>Persists and retrieves sync watermarks for integration sources.</summary>
public interface IIntegrationCheckpointStore
{
    Task<IntegrationCheckpoint?> GetAsync(string sourceSystem, string principalId, CancellationToken ct);
    Task SaveAsync(IntegrationCheckpoint checkpoint, CancellationToken ct);
}
