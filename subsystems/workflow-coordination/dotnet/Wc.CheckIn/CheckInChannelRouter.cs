using System.Collections.Concurrent;
using Km.Store;
using Wc.Contracts;

namespace Wc.CheckIn;

using CheckIn = global::Wc.Contracts.CheckIn;

/// <summary>
/// Routes a check-in digest to the correct ICheckInTransport based on the owner's stored
/// channel preference. Implements ICheckInTransport so it is a transparent drop-in wherever
/// a transport is accepted — callers (RaiseCheckInsStage, GapCheckInStage) need no changes.
///
/// Routing rules:
///   1. No Slack factory configured → always Email (Slack bot token absent or not set).
///   2. Owner has no stored preference → Email (default).
///   3. Owner preference is Email → Email.
///   4. Owner preference is Slack with a valid destination → Slack transport for that destination.
///
/// Thread-safe: Slack transport instances are cached per destination.
///
/// Two constructors:
///   Production: CheckInChannelRouter(prefStore, emailTransport, slackBotToken?)
///     Creates SlackCheckInTransport instances internally using a new HttpClient.
///     Kozmo.Api never touches HttpClient directly (banned there; lives here in Wc.CheckIn).
///
///   Test/custom: CheckInChannelRouter(prefStore, emailTransport, Func{string, ICheckInTransport})
///     Accepts a factory for injecting mock transports in unit tests.
/// </summary>
public sealed class CheckInChannelRouter : ICheckInTransport
{
    private readonly IOwnerChannelPrefStore           _prefStore;
    private readonly ICheckInTransport                _emailTransport;
    private readonly Func<string, ICheckInTransport>? _slackFactory;
    private readonly ConcurrentDictionary<string, ICheckInTransport> _cache = new();

    /// <summary>
    /// Production constructor. When <paramref name="slackBotToken"/> is non-null, owners with
    /// a Slack preference are routed to a <see cref="SlackCheckInTransport"/> for their destination.
    /// HttpClient is created internally — never exposed to callers.
    /// </summary>
    public CheckInChannelRouter(
        IOwnerChannelPrefStore prefStore,
        ICheckInTransport      emailTransport,
        string?                slackBotToken      = null,
        Func<Guid, string?>?   vendorNameResolver = null)
    {
        _prefStore      = prefStore;
        _emailTransport = emailTransport;

        if (slackBotToken is not null)
        {
            var http     = new System.Net.Http.HttpClient();
            var token    = slackBotToken;
            var resolver = vendorNameResolver;
            _slackFactory = dest => new SlackCheckInTransport(http, token, dest, resolver);
        }
    }

    /// <summary>
    /// Test / custom constructor. <paramref name="slackFactory"/> is called with the destination
    /// string and must return the transport to use for that destination.
    /// </summary>
    public CheckInChannelRouter(
        IOwnerChannelPrefStore          prefStore,
        ICheckInTransport               emailTransport,
        Func<string, ICheckInTransport>  slackFactory)
    {
        _prefStore      = prefStore;
        _emailTransport = emailTransport;
        _slackFactory   = slackFactory;
    }

    /// <summary>
    /// Returns the transport for the given owner: Slack when a preference exists and Slack is
    /// configured; Email in all other cases.
    /// </summary>
    public async Task<ICheckInTransport> ResolveAsync(string ownerId, CancellationToken ct = default)
    {
        if (_slackFactory is null) return _emailTransport;

        var row = await _prefStore.GetOwnerChannelPrefAsync(ownerId, ct);
        if (row is null || row.Channel != "Slack" || row.SlackDestination is null)
            return _emailTransport;

        return _cache.GetOrAdd(row.SlackDestination, _slackFactory);
    }

    /// <inheritdoc/>
    public async Task SendAsync(IReadOnlyList<CheckIn> checkIns, CancellationToken ct = default)
    {
        if (checkIns.Count == 0) return;
        var transport = await ResolveAsync(checkIns[0].Owner, ct);
        await transport.SendAsync(checkIns, ct);
    }
}
