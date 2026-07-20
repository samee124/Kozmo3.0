namespace Km.Store;

/// <summary>
/// Storage interface for owner channel preferences (owner_channel_prefs table).
/// Uses primitive/BCL types only — no dependency on Wc.Contracts.
/// Implemented by SqliteEntityStore (shared connection, shared schema).
/// Default when no row exists for an owner: Email channel.
/// </summary>
public interface IOwnerChannelPrefStore
{
    Task SaveOwnerChannelPrefAsync(OwnerChannelPrefRow row, CancellationToken ct = default);
    Task<OwnerChannelPrefRow?> GetOwnerChannelPrefAsync(string ownerId, CancellationToken ct = default);
    Task<IReadOnlyList<OwnerChannelPrefRow>> GetAllOwnerChannelPrefsAsync(CancellationToken ct = default);
}

/// <summary>Storage row for an owner's channel preference (owner_channel_prefs table).</summary>
public sealed record OwnerChannelPrefRow(
    string  OwnerId,
    string  Channel,          // "Email" | "Slack"
    string? SlackDestination  // Slack channel id, "#name", or user id for DM; null when Email
);
