using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ii.Contracts;
using Ii.Decay;
using Ii.Index;
using Ii.Observation;
using Ii.Posture;
using Ii.Rubric;
using Ii.Spine;
using Km.Store;
using Kozmo.Contracts;
using Kozmo.Contracts.Config;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Wc.CheckIn;
using Wc.Contracts;
using Xunit;

namespace Kozmo.Api.Tests;

using CheckIn = global::Wc.Contracts.CheckIn;

// ── Collection ───────────────────────────────────────────────────────────────

[CollectionDefinition("SlackPhase3Tests")]
public class SlackPhase3Collection : ICollectionFixture<SlackPhase3Fixture> { }

// ── Capturing HTTP handler (no real Slack API calls) ─────────────────────────

internal sealed class CapturingHomeHandler : HttpMessageHandler
{
    public List<(string Url, string Body)> Calls { get; } = new();

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is not null
            ? await request.Content.ReadAsStringAsync(ct)
            : "";
        Calls.Add((request.RequestUri!.ToString(), body));
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"ok\":true}", Encoding.UTF8, "application/json")
        };
    }
}

// ── In-memory check-in store for Phase 3 tests ───────────────────────────────

public sealed class InMemoryCheckInStoreForPhase3 : ICheckInStore
{
    private readonly Dictionary<Guid, CheckIn> _store = new();

    public void Seed(CheckIn ci) => _store[ci.CheckInId] = ci;

    public Task SaveAsync(CheckIn checkIn, CancellationToken ct = default)
    {
        _store[checkIn.CheckInId] = checkIn;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<CheckIn>> GetOpenAsync(CancellationToken ct = default)
    {
        IReadOnlyList<CheckIn> r = _store.Values.Where(c => c.Status == PendingStatus.OPEN).ToList();
        return Task.FromResult(r);
    }

    public Task<CheckIn?> GetAsync(Guid id, CancellationToken ct = default)
        => Task.FromResult(_store.TryGetValue(id, out var c) ? c : (CheckIn?)null);

    public Task<IReadOnlyList<CheckIn>> GetResolvedForVendorAsync(Guid vendorId, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<CheckIn>>([]);
}

// ── Read-only IIiFacade stub — throws on any write path ──────────────────────
// Both new endpoints are read-only. Using a stub that throws on write methods
// means any accidental write call surfaces as a test failure rather than silent success.

internal sealed class ReadOnlyFacadeStub : IIiFacade
{
    // Read methods return null (no data seeded — vendor "not assessed" is valid state)
    public Task<PostureAssignment?> GetPostureAsync(Guid entityId, CancellationToken ct = default)
        => Task.FromResult<PostureAssignment?>(null);

    public Task<EntityIndex?> GetIndexAsync(Guid entityId, CancellationToken ct = default)
        => Task.FromResult<EntityIndex?>(null);

    public Task<IReadOnlyList<Belief>> GetBeliefsAsync(Guid entityId, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<Belief>>([]);

    public Task<ReasoningTrail?> GetReasoningTrailAsync(Guid entityId, CancellationToken ct = default)
        => Task.FromResult<ReasoningTrail?>(null);

    public Task<IReadOnlyList<TrajectoryPoint>> GetTrajectoryAsync(Guid entityId, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<TrajectoryPoint>>([]);

    // Write paths — these must NEVER be called from Phase 3 endpoints
    public Task<Guid> SubmitSignalAsync(Signal signal, CancellationToken ct = default)
        => throw new InvalidOperationException("SubmitSignalAsync must not be called from a read-only endpoint.");

    public Task ResetAsync(CancellationToken ct = default)
        => throw new InvalidOperationException("ResetAsync must not be called from a read-only endpoint.");

    public Task<VendorJudgement?> RecomputeVendorAsync(Guid entityId, CancellationToken ct = default)
        => throw new InvalidOperationException("RecomputeVendorAsync must not be called from a read-only endpoint.");
}

// ── Fixture ──────────────────────────────────────────────────────────────────

public sealed class SlackPhase3Fixture : WebApplicationFactory<Program>, IAsyncLifetime
{
    // Must match SlackInteractivityFixture.TestSigningSecret — both fixtures write to the same
    // KOZMO_SLACK_SIGNING_SECRET env var, so they must agree on the value to avoid a race
    // when the full test suite runs all Slack fixture collections simultaneously.
    internal const string TestSigningSecret = SlackInteractivityFixture.TestSigningSecret;

    internal InMemoryCheckInStoreForPhase3 Store        { get; } = new();
    internal CapturingHomeHandler          HomeHandler  { get; } = new();
    internal SlackHomeTabPublisher         HomePublisher { get; private set; } = null!;

    // Vendor IDs from ApiFixture.BuildTestRegistry — used in tests
    internal static readonly Guid CloudwaveId = Guid.Parse("eeeeeeee-0001-0000-0000-000000000001");

    public async Task InitializeAsync()
    {
        Environment.SetEnvironmentVariable("KOZMO_SLACK_SIGNING_SECRET", TestSigningSecret);
        Environment.SetEnvironmentVariable("BREVO_SMTP_KEY", null);
        await Task.CompletedTask;
    }

    public new async Task DisposeAsync()
    {
        Environment.SetEnvironmentVariable("KOZMO_SLACK_SIGNING_SECRET", null);
        await base.DisposeAsync();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureServices(services =>
        {
            // Replace IIiFacade with read-only stub (throws on any write)
            var oldFacade = services.SingleOrDefault(d => d.ServiceType == typeof(IIiFacade));
            if (oldFacade != null) services.Remove(oldFacade);
            services.AddSingleton<IIiFacade>(new ReadOnlyFacadeStub());

            // Remove production SqliteEntityStore; add shared in-memory one
            var oldStore = services.SingleOrDefault(d => d.ServiceType == typeof(SqliteEntityStore));
            if (oldStore != null) services.Remove(oldStore);
            services.AddSingleton(new SqliteEntityStore("Data Source=:memory:"));

            // Replace ICheckInStore with seeded in-memory store
            var oldCs = services.SingleOrDefault(d => d.ServiceType == typeof(ICheckInStore));
            if (oldCs != null) services.Remove(oldCs);
            services.AddSingleton<ICheckInStore>(Store);

            // Replace SlackHomeTabPublisher with a capturing version so no real HTTP is made
            var oldPub = services.SingleOrDefault(d => d.ServiceType == typeof(SlackHomeTabPublisher));
            if (oldPub != null) services.Remove(oldPub);
            HomePublisher = new SlackHomeTabPublisher(new HttpClient(HomeHandler), "xoxb-test");
            services.AddSingleton(HomePublisher);
        });
    }
}

// ── Signature helper (same HMACSHA256 scheme as Phase 2 tests) ───────────────

file static class SlackPhase3Signer
{
    internal static string Sign(string body, string timestamp, string secret = SlackPhase3Fixture.TestSigningSecret)
    {
        var baseStr  = $"v0:{timestamp}:{body}";
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(baseStr));
        return "v0=" + Convert.ToHexString(hash).ToLowerInvariant();
    }

    internal static string Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
}

// ── Slash command tests ───────────────────────────────────────────────────────

[Collection("SlackPhase3Tests")]
public sealed class SlackCommandTests
{
    private static readonly Guid   VendorId = SlackPhase3Fixture.CloudwaveId;
    private static readonly Guid   RunId    = new("CCCCCCCC-0000-0000-0000-000000000001");
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly SlackPhase3Fixture _fx;
    public SlackCommandTests(SlackPhase3Fixture fx) => _fx = fx;

    private CheckIn OpenCheckIn(string question = "Does the vendor have a documented SLA?") => new(
        CheckInId:      Guid.NewGuid(),
        VendorId:       VendorId,
        ProgramRunId:   RunId,
        Kind:           CheckInKind.DIMENSION_GAP,
        Question:       question,
        ResponseShape:  ResponseShape.YES_NO,
        TargetField:    null,
        Owner:          "owner@test",
        Status:         PendingStatus.OPEN,
        RaisedAt:       Now,
        AnsweredAt:     null,
        ExpiresAt:      null,
        ResponseValue:  null);

    private static HttpRequestMessage CommandRequest(string text, string userId = "U99001")
    {
        var form      = $"command=%2Fkozmo&text={Uri.EscapeDataString(text)}&user_id={userId}&channel_id=C001";
        var timestamp = SlackPhase3Signer.Now();
        var sig       = SlackPhase3Signer.Sign(form, timestamp);
        var req       = new HttpRequestMessage(HttpMethod.Post, "/slack/command");
        req.Headers.Add("X-Slack-Request-Timestamp", timestamp);
        req.Headers.Add("X-Slack-Signature", sig);
        req.Content = new StringContent(form, Encoding.UTF8, "application/x-www-form-urlencoded");
        return req;
    }

    // ── /kozmo pending → lists caller's open check-ins ──────────────────────

    [Fact]
    public async Task Pending_ValidSig_Returns200WithCheckInQuestion()
    {
        var ci = OpenCheckIn("Does the vendor have a documented SLA?");
        _fx.Store.Seed(ci);

        using var client = _fx.CreateClient();
        using var req    = CommandRequest("pending");
        var resp = await client.SendAsync(req);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("Does the vendor have a documented SLA?", body);
        Assert.Contains("ephemeral", body);
    }

    // ── /kozmo vendor <known> → 200 with posture card ───────────────────────

    [Fact]
    public async Task VendorCommand_KnownVendor_Returns200WithVendorName()
    {
        using var client = _fx.CreateClient();
        using var req    = CommandRequest("vendor Cloudwave");
        var resp = await client.SendAsync(req);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        // Vendor name must appear in the card
        Assert.Contains("Cloudwave", body);
        Assert.Contains("ephemeral", body);
    }

    // ── /kozmo vendor <unknown> → 200 "not found" ───────────────────────────

    [Fact]
    public async Task VendorCommand_UnknownVendor_Returns200NotFound()
    {
        using var client = _fx.CreateClient();
        using var req    = CommandRequest("vendor ZzZzNonExistentVendorXxx");
        var resp = await client.SendAsync(req);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("No vendor found", body);
    }

    // ── /kozmo help → 200 usage message ─────────────────────────────────────

    [Fact]
    public async Task Help_Returns200UsageMessage()
    {
        using var client = _fx.CreateClient();
        using var req    = CommandRequest("help");
        var resp = await client.SendAsync(req);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("pending", body);
        Assert.Contains("vendor", body);
    }

    // ── Unknown subcommand → 200 usage message ──────────────────────────────

    [Fact]
    public async Task UnknownSubcommand_Returns200UsageMessage()
    {
        using var client = _fx.CreateClient();
        using var req    = CommandRequest("foobar");
        var resp = await client.SendAsync(req);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("pending", body);
    }

    // ── Tampered / stale signature → 401 ────────────────────────────────────

    [Fact]
    public async Task TamperedSignature_Returns401()
    {
        var form      = "command=%2Fkozmo&text=pending&user_id=U001&channel_id=C001";
        var timestamp = SlackPhase3Signer.Now();
        var sig       = SlackPhase3Signer.Sign(form, timestamp);
        var tampered  = form + "&injected=true";

        using var client = _fx.CreateClient();
        using var req    = new HttpRequestMessage(HttpMethod.Post, "/slack/command");
        req.Headers.Add("X-Slack-Request-Timestamp", timestamp);
        req.Headers.Add("X-Slack-Signature", sig);
        req.Content = new StringContent(tampered, Encoding.UTF8, "application/x-www-form-urlencoded");
        var resp = await client.SendAsync(req);

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task StaleSignature_Returns401()
    {
        var form    = "command=%2Fkozmo&text=pending&user_id=U001&channel_id=C001";
        var staleTs = (DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 400).ToString();
        var sig     = SlackPhase3Signer.Sign(form, staleTs);

        using var client = _fx.CreateClient();
        using var req    = new HttpRequestMessage(HttpMethod.Post, "/slack/command");
        req.Headers.Add("X-Slack-Request-Timestamp", staleTs);
        req.Headers.Add("X-Slack-Signature", sig);
        req.Content = new StringContent(form, Encoding.UTF8, "application/x-www-form-urlencoded");
        var resp = await client.SendAsync(req);

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    // ── Read-only proof: check-in remains OPEN after a /pending command ──────
    // If ProcessAnswerAsync were ever called, the check-in status would change.

    [Fact]
    public async Task Pending_ReadOnlyProof_CheckInRemainsOpen()
    {
        var ci = OpenCheckIn("Is the SLA current?");
        _fx.Store.Seed(ci);

        using var client = _fx.CreateClient();
        using var req    = CommandRequest("pending");
        await client.SendAsync(req);

        // Check-in must still be OPEN — the command endpoint is read-only
        var after = await _fx.Store.GetAsync(ci.CheckInId);
        Assert.NotNull(after);
        Assert.Equal(PendingStatus.OPEN, after!.Status);
    }
}

// ── Home tab (events) tests ───────────────────────────────────────────────────

[Collection("SlackPhase3Tests")]
public sealed class SlackEventsTests
{
    private static readonly Guid   VendorId = SlackPhase3Fixture.CloudwaveId;
    private static readonly Guid   RunId    = new("DDDDDDDD-0000-0000-0000-000000000001");
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly SlackPhase3Fixture _fx;
    public SlackEventsTests(SlackPhase3Fixture fx) => _fx = fx;

    private CheckIn OpenCheckIn(string q = "Does the vendor have an SLA?") => new(
        CheckInId:      Guid.NewGuid(),
        VendorId:       VendorId,
        ProgramRunId:   RunId,
        Kind:           CheckInKind.DIMENSION_GAP,
        Question:       q,
        ResponseShape:  ResponseShape.YES_NO,
        TargetField:    null,
        Owner:          "owner@test",
        Status:         PendingStatus.OPEN,
        RaisedAt:       Now,
        AnsweredAt:     null,
        ExpiresAt:      null,
        ResponseValue:  null);

    private static HttpRequestMessage SignedJsonRequest(string json)
    {
        var timestamp = SlackPhase3Signer.Now();
        var sig       = SlackPhase3Signer.Sign(json, timestamp);
        var req       = new HttpRequestMessage(HttpMethod.Post, "/slack/events");
        req.Headers.Add("X-Slack-Request-Timestamp", timestamp);
        req.Headers.Add("X-Slack-Signature", sig);
        req.Content = new StringContent(json, Encoding.UTF8, "application/json");
        return req;
    }

    // ── url_verification challenge echoed back (no signature required) ───────

    [Fact]
    public async Task UrlVerification_EchosChallenge_NoSignatureRequired()
    {
        const string challenge = "3eZbrw1aBm2rZgRNFdxV2595E9CY3gmdALWMmHkvFXO7tYXAluzUDVwi";
        var json = JsonSerializer.Serialize(new { type = "url_verification", challenge });

        using var client = _fx.CreateClient();
        using var req    = new HttpRequestMessage(HttpMethod.Post, "/slack/events");
        // Deliberately NO signature headers — challenge must work without them
        req.Content = new StringContent(json, Encoding.UTF8, "application/json");
        var resp = await client.SendAsync(req);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains(challenge, body);
    }

    // ── app_home_opened → 200 + views.publish called ────────────────────────

    [Fact]
    public async Task AppHomeOpened_ValidSig_Returns200AndPublishesHome()
    {
        var ci = OpenCheckIn("Is the uptime SLA documented?");
        _fx.Store.Seed(ci);
        _fx.HomeHandler.Calls.Clear();

        var json = JsonSerializer.Serialize(new
        {
            type  = "event_callback",
            @event = new { type = "app_home_opened", user ="U55555" }
        });

        using var client = _fx.CreateClient();
        using var req    = SignedJsonRequest(json);
        var resp = await client.SendAsync(req);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        // views.publish must have been called exactly once
        Assert.Single(_fx.HomeHandler.Calls);
        var (url, publishBody) = _fx.HomeHandler.Calls[0];
        Assert.Contains("views.publish", url);
        // Published view must include the check-in question
        Assert.Contains("Is the uptime SLA documented?", publishBody);
    }

    // ── Bad / missing signature → 401, views.publish not called ─────────────

    [Fact]
    public async Task BadSignature_Returns401_ViewsPublishNotCalled()
    {
        _fx.HomeHandler.Calls.Clear();

        var json = JsonSerializer.Serialize(new
        {
            type  = "event_callback",
            @event = new { type = "app_home_opened", user ="U88888" }
        });

        using var client = _fx.CreateClient();
        using var req    = new HttpRequestMessage(HttpMethod.Post, "/slack/events");
        req.Headers.Add("X-Slack-Request-Timestamp", SlackPhase3Signer.Now());
        req.Headers.Add("X-Slack-Signature", "v0=badhash");
        req.Content = new StringContent(json, Encoding.UTF8, "application/json");
        var resp = await client.SendAsync(req);

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        Assert.Empty(_fx.HomeHandler.Calls);
    }

    // ── Read-only proof: check-in remains OPEN after app_home_opened ─────────

    [Fact]
    public async Task AppHomeOpened_ReadOnlyProof_CheckInRemainsOpen()
    {
        var ci = OpenCheckIn("Is the contract current?");
        _fx.Store.Seed(ci);

        var json = JsonSerializer.Serialize(new
        {
            type  = "event_callback",
            @event = new { type = "app_home_opened", user ="U11111" }
        });

        using var client = _fx.CreateClient();
        using var req    = SignedJsonRequest(json);
        await client.SendAsync(req);

        var after = await _fx.Store.GetAsync(ci.CheckInId);
        Assert.NotNull(after);
        Assert.Equal(PendingStatus.OPEN, after!.Status);
    }
}
