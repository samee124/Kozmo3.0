using Km.Store;
using Wc.CheckIn;
using Wc.Contracts;
using Xunit;

namespace Wc.Tests;

using CheckIn = global::Wc.Contracts.CheckIn;

/// <summary>
/// Tests for CheckInChannelRouter — routing decisions based on owner channel preferences.
/// Uses in-memory store and tracking transports; never makes real HTTP calls.
/// </summary>
public sealed class CheckInChannelRouterTests
{
    private static readonly Guid   VendorId = new("AAAAAAAA-0000-0000-0000-000000000001");
    private static readonly Guid   RunId    = new("BBBBBBBB-0000-0000-0000-000000000001");
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static CheckIn MakeCheckIn(string owner) => new(
        CheckInId:      Guid.NewGuid(),
        VendorId:       VendorId,
        ProgramRunId:   RunId,
        Kind:           CheckInKind.DIMENSION_GAP,
        Question:       "Test question",
        ResponseShape:  ResponseShape.YES_NO,
        TargetField:    null,
        Owner:          owner,
        Status:         PendingStatus.OPEN,
        RaisedAt:       Now,
        AnsweredAt:     null,
        ExpiresAt:      null,
        ResponseValue:  null);

    // ── Router: owner with Slack pref → Slack transport ──────────────────────

    [Fact]
    public async Task Resolve_SlackPref_ReturnsSlackTransport()
    {
        var store        = new InMemoryPrefStore();
        var emailTransport = new TrackingTransport();
        var slackTransport = new TrackingTransport();

        await store.SaveOwnerChannelPrefAsync(new OwnerChannelPrefRow(
            OwnerId:          "owner@test",
            Channel:          "Slack",
            SlackDestination: "C0123456"));

        var router = new CheckInChannelRouter(store, emailTransport,
            dest => slackTransport);

        var resolved = await router.ResolveAsync("owner@test");

        Assert.Same(slackTransport, resolved);
    }

    // ── Router: owner with no pref → email transport ──────────────────────

    [Fact]
    public async Task Resolve_NoPref_ReturnsEmailTransport()
    {
        var store          = new InMemoryPrefStore();
        var emailTransport = new TrackingTransport();
        var slackTransport = new TrackingTransport();

        // No row stored — email is the default
        var router = new CheckInChannelRouter(store, emailTransport,
            dest => slackTransport);

        var resolved = await router.ResolveAsync("owner@test");

        Assert.Same(emailTransport, resolved);
    }

    // ── Router: owner with Email pref → email transport ──────────────────

    [Fact]
    public async Task Resolve_EmailPref_ReturnsEmailTransport()
    {
        var store          = new InMemoryPrefStore();
        var emailTransport = new TrackingTransport();
        var slackTransport = new TrackingTransport();

        await store.SaveOwnerChannelPrefAsync(new OwnerChannelPrefRow(
            OwnerId:          "owner@test",
            Channel:          "Email",
            SlackDestination: null));

        var router = new CheckInChannelRouter(store, emailTransport,
            dest => slackTransport);

        var resolved = await router.ResolveAsync("owner@test");

        Assert.Same(emailTransport, resolved);
    }

    // ── Router: DM destination resolves (different destination, same transport type) ──

    [Fact]
    public async Task Resolve_DmDestination_ReturnsSlackTransport()
    {
        var store          = new InMemoryPrefStore();
        var emailTransport = new TrackingTransport();
        var capturedDest   = (string?)null;

        await store.SaveOwnerChannelPrefAsync(new OwnerChannelPrefRow(
            OwnerId:          "owner@test",
            Channel:          "Slack",
            SlackDestination: "U0987654"));  // user id = DM

        var router = new CheckInChannelRouter(store, emailTransport, dest =>
        {
            capturedDest = dest;
            return new TrackingTransport();
        });

        await router.ResolveAsync("owner@test");

        Assert.Equal("U0987654", capturedDest);
    }

    // ── Router: channel destination resolves ─────────────────────────────

    [Fact]
    public async Task Resolve_ChannelDestination_ReturnsSlackTransport()
    {
        var store          = new InMemoryPrefStore();
        var emailTransport = new TrackingTransport();
        var capturedDest   = (string?)null;

        await store.SaveOwnerChannelPrefAsync(new OwnerChannelPrefRow(
            OwnerId:          "owner@test",
            Channel:          "Slack",
            SlackDestination: "#kozmo-checkins"));

        var router = new CheckInChannelRouter(store, emailTransport, dest =>
        {
            capturedDest = dest;
            return new TrackingTransport();
        });

        await router.ResolveAsync("owner@test");

        Assert.Equal("#kozmo-checkins", capturedDest);
    }

    // ── Router: no Slack factory → always email even with Slack pref ─────

    [Fact]
    public async Task Resolve_NoSlackFactory_AlwaysEmail()
    {
        var store          = new InMemoryPrefStore();
        var emailTransport = new TrackingTransport();

        await store.SaveOwnerChannelPrefAsync(new OwnerChannelPrefRow(
            OwnerId:          "owner@test",
            Channel:          "Slack",
            SlackDestination: "C0123456"));

        // Production ctor with null bot token — no Slack factory
        var router = new CheckInChannelRouter(store, emailTransport, slackBotToken: null);

        var resolved = await router.ResolveAsync("owner@test");

        Assert.Same(emailTransport, resolved);
    }

    // ── Router: SendAsync dispatches to correct transport ─────────────────

    [Fact]
    public async Task SendAsync_SlackOwner_CallsSlackTransport()
    {
        var store          = new InMemoryPrefStore();
        var emailTransport = new TrackingTransport();
        var slackTransport = new TrackingTransport();

        await store.SaveOwnerChannelPrefAsync(new OwnerChannelPrefRow(
            OwnerId:          "slack@owner",
            Channel:          "Slack",
            SlackDestination: "C0123456"));

        var router = new CheckInChannelRouter(store, emailTransport,
            dest => slackTransport);

        var ci = MakeCheckIn("slack@owner");
        await router.SendAsync([ci]);

        Assert.Single(slackTransport.Received);
        Assert.Empty(emailTransport.Received);
    }

    [Fact]
    public async Task SendAsync_EmailOwner_CallsEmailTransport()
    {
        var store          = new InMemoryPrefStore();
        var emailTransport = new TrackingTransport();
        var slackTransport = new TrackingTransport();

        // No pref stored — defaults to email
        var router = new CheckInChannelRouter(store, emailTransport,
            dest => slackTransport);

        var ci = MakeCheckIn("email@owner");
        await router.SendAsync([ci]);

        Assert.Single(emailTransport.Received);
        Assert.Empty(slackTransport.Received);
    }

    // ── Router: same destination cached (factory called once) ─────────────

    [Fact]
    public async Task Resolve_SameDestination_FactoryCalledOnce()
    {
        var store          = new InMemoryPrefStore();
        var emailTransport = new TrackingTransport();
        var factoryCalls   = 0;

        await store.SaveOwnerChannelPrefAsync(new OwnerChannelPrefRow(
            OwnerId:          "owner@test",
            Channel:          "Slack",
            SlackDestination: "C0123456"));

        var router = new CheckInChannelRouter(store, emailTransport, dest =>
        {
            factoryCalls++;
            return new TrackingTransport();
        });

        await router.ResolveAsync("owner@test");
        await router.ResolveAsync("owner@test");

        Assert.Equal(1, factoryCalls);
    }
}

// ── Test doubles ─────────────────────────────────────────────────────────────

internal sealed class InMemoryPrefStore : IOwnerChannelPrefStore
{
    private readonly Dictionary<string, OwnerChannelPrefRow> _store = new(StringComparer.Ordinal);

    public Task SaveOwnerChannelPrefAsync(OwnerChannelPrefRow row, CancellationToken ct = default)
    {
        _store[row.OwnerId] = row;
        return Task.CompletedTask;
    }

    public Task<OwnerChannelPrefRow?> GetOwnerChannelPrefAsync(string ownerId, CancellationToken ct = default)
        => Task.FromResult(_store.TryGetValue(ownerId, out var r) ? r : (OwnerChannelPrefRow?)null);

    public Task<IReadOnlyList<OwnerChannelPrefRow>> GetAllOwnerChannelPrefsAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<OwnerChannelPrefRow>>(_store.Values.ToList());
}

internal sealed class TrackingTransport : ICheckInTransport
{
    public List<IReadOnlyList<CheckIn>> Received { get; } = new();

    public Task SendAsync(IReadOnlyList<CheckIn> checkIns, CancellationToken ct = default)
    {
        Received.Add(checkIns);
        return Task.CompletedTask;
    }
}
