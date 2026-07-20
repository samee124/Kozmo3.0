using If.Contracts;

namespace If.MicrosoftGraph;

/// <summary>Microsoft Graph-backed sync checkpoint persistence.</summary>
public sealed class GraphSyncCheckpointStore : IIntegrationCheckpointStore
{
    /// <inheritdoc/>
    public Task<IntegrationCheckpoint?> GetAsync(string sourceSystem, string principalId, CancellationToken ct)
        => throw new NotImplementedException();

    /// <inheritdoc/>
    public Task SaveAsync(IntegrationCheckpoint checkpoint, CancellationToken ct)
        => throw new NotImplementedException();
}
