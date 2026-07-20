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

// ── Collection ──────────────────────────────────────────────────────────────

[CollectionDefinition("SlackTests")]
public class SlackTestsCollection : ICollectionFixture<SlackInteractivityFixture> { }

// ── Fixture ──────────────────────────────────────────────────────────────────

public sealed class SlackInteractivityFixture : WebApplicationFactory<Program>, IAsyncLifetime
{
    internal const string TestSigningSecret = "slack-test-signing-secret-32bytes!";

    internal InMemoryCheckInStoreForSlack Store      { get; } = new();
    internal SqliteEntityStore            EntityStore { get; } = new("Data Source=:memory:");

    public async Task InitializeAsync()
    {
        // Set the signing secret so Program.cs picks it up at build time
        Environment.SetEnvironmentVariable("KOZMO_SLACK_SIGNING_SECRET", TestSigningSecret);
        // Ensure Brevo is not active (avoids unrelated side effects)
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
            // Replace IIiFacade — use EntityStore so the facade, the write service (DI-injected),
            // and the test assertions all share the same in-memory SQLite instance.
            var oldFacade = services.SingleOrDefault(d => d.ServiceType == typeof(IIiFacade));
            if (oldFacade != null) services.Remove(oldFacade);

            // Remove the production SqliteEntityStore registration before adding the test one.
            var oldEntityStore = services.SingleOrDefault(d => d.ServiceType == typeof(SqliteEntityStore));
            if (oldEntityStore != null) services.Remove(oldEntityStore);

            var profile  = ApiFixture.FindAndLoadProfileInternal();
            var registry = ApiFixture.BuildTestRegistry();

            services.AddSingleton(EntityStore);
            services.AddSingleton<IIiFacade>(new IiFacade(
                new ObservationModule(), new RubricModule(), new IndexModule(),
                new PostureModule(), new DecayEngine(),
                EntityStore, profile, registry, DemoClock.Fixed));

            // Replace ICheckInStore with in-memory store seeded with an OPEN check-in
            var oldStore = services.SingleOrDefault(d => d.ServiceType == typeof(ICheckInStore));
            if (oldStore != null) services.Remove(oldStore);
            services.AddSingleton<ICheckInStore>(Store);
        });
    }
}

// ── In-memory check-in store for Slack tests ─────────────────────────────────

public sealed class InMemoryCheckInStoreForSlack : ICheckInStore
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

// ── Tests ─────────────────────────────────────────────────────────────────────

[Collection("SlackTests")]
public sealed class SlackInteractivityTests
{
    private static readonly Guid   VendorId = new("AAAAAAAA-0000-0000-0000-000000000001");
    private static readonly Guid   RunId    = new("BBBBBBBB-0000-0000-0000-000000000001");
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly SlackInteractivityFixture _fx;

    public SlackInteractivityTests(SlackInteractivityFixture fx) => _fx = fx;

    private static CheckIn OpenCheckIn(Guid id, ResponseShape shape = ResponseShape.YES_NO) => new(
        CheckInId:      id,
        VendorId:       VendorId,
        ProgramRunId:   RunId,
        Kind:           CheckInKind.DIMENSION_GAP,
        Question:       "Does the vendor have a documented uptime SLA?",
        ResponseShape:  shape,
        TargetField:    null,
        Owner:          "owner@test",
        Status:         PendingStatus.OPEN,
        RaisedAt:       Now,
        AnsweredAt:     null,
        ExpiresAt:      null,
        ResponseValue:  null);

    /// <summary>Builds a valid Slack signature for the given body + timestamp.</summary>
    private static string Sign(string body, string timestamp)
    {
        var baseString  = $"v0:{timestamp}:{body}";
        var secretBytes = Encoding.UTF8.GetBytes(SlackInteractivityFixture.TestSigningSecret);
        var baseBytes   = Encoding.UTF8.GetBytes(baseString);
        using var hmac  = new HMACSHA256(secretBytes);
        var hash = hmac.ComputeHash(baseBytes);
        return "v0=" + Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// Builds the URL-encoded form body Slack sends for a block_actions button click.
    /// </summary>
    private static string BuildSlackPayload(Guid checkInId, string answer, string userId = "U12345")
    {
        var payload = new
        {
            type = "block_actions",
            user = new { id = userId },
            actions = new[]
            {
                new
                {
                    action_id = "yes_btn",
                    value     = JsonSerializer.Serialize(new
                    {
                        checkInId = checkInId.ToString(),
                        answer
                    })
                }
            }
        };
        var json    = JsonSerializer.Serialize(payload);
        var encoded = Uri.EscapeDataString(json);
        return $"payload={encoded}";
    }

    // ── Valid signature + valid payload → 200, message replaced ──────────────

    [Fact]
    public async Task ValidRequest_Returns200_WithReplaceOriginal()
    {
        var id = Guid.NewGuid();
        _fx.Store.Seed(OpenCheckIn(id));

        var formBody  = BuildSlackPayload(id, "true", "U12345");
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var signature = Sign(formBody, timestamp);

        using var client = _fx.CreateClient();
        using var req    = new HttpRequestMessage(HttpMethod.Post, "/slack/interactivity");
        req.Headers.Add("X-Slack-Request-Timestamp", timestamp);
        req.Headers.Add("X-Slack-Signature", signature);
        req.Content = new StringContent(formBody, Encoding.UTF8, "application/x-www-form-urlencoded");

        var resp = await client.SendAsync(req);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("replace_original", body);
    }

    // ── Tampered body → 401, ProcessAnswerAsync never called ─────────────────

    [Fact]
    public async Task TamperedBody_Returns401()
    {
        var id = Guid.NewGuid();
        _fx.Store.Seed(OpenCheckIn(id));

        var formBody  = BuildSlackPayload(id, "true");
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var signature = Sign(formBody, timestamp);

        // Tamper the body AFTER signing
        var tamperedBody = formBody + "&extra=injected";

        using var client = _fx.CreateClient();
        using var req    = new HttpRequestMessage(HttpMethod.Post, "/slack/interactivity");
        req.Headers.Add("X-Slack-Request-Timestamp", timestamp);
        req.Headers.Add("X-Slack-Signature", signature);
        req.Content = new StringContent(tamperedBody, Encoding.UTF8, "application/x-www-form-urlencoded");

        var resp = await client.SendAsync(req);

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    // ── Stale timestamp → 401 ─────────────────────────────────────────────────

    [Fact]
    public async Task StaleTimestamp_Returns401()
    {
        var id = Guid.NewGuid();
        _fx.Store.Seed(OpenCheckIn(id));

        var formBody  = BuildSlackPayload(id, "true");
        // Timestamp older than 5 minutes
        var staleTs   = (DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 400).ToString();
        var signature = Sign(formBody, staleTs);

        using var client = _fx.CreateClient();
        using var req    = new HttpRequestMessage(HttpMethod.Post, "/slack/interactivity");
        req.Headers.Add("X-Slack-Request-Timestamp", staleTs);
        req.Headers.Add("X-Slack-Signature", signature);
        req.Content = new StringContent(formBody, Encoding.UTF8, "application/x-www-form-urlencoded");

        var resp = await client.SendAsync(req);

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    // ── Missing signature → 401 ───────────────────────────────────────────────

    [Fact]
    public async Task MissingSignature_Returns401()
    {
        var id       = Guid.NewGuid();
        _fx.Store.Seed(OpenCheckIn(id));
        var formBody  = BuildSlackPayload(id, "true");
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();

        using var client = _fx.CreateClient();
        using var req    = new HttpRequestMessage(HttpMethod.Post, "/slack/interactivity");
        req.Headers.Add("X-Slack-Request-Timestamp", timestamp);
        // No X-Slack-Signature header
        req.Content = new StringContent(formBody, Encoding.UTF8, "application/x-www-form-urlencoded");

        var resp = await client.SendAsync(req);

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    // ── Idempotency: second click → AlreadyAnswered, still 200 ───────────────

    [Fact]
    public async Task SecondClick_AlreadyAnswered_Still200()
    {
        var id = Guid.NewGuid();
        _fx.Store.Seed(OpenCheckIn(id));

        var formBody  = BuildSlackPayload(id, "true");
        var ts1       = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var sig1      = Sign(formBody, ts1);

        using var client = _fx.CreateClient();

        // First click
        using var req1 = new HttpRequestMessage(HttpMethod.Post, "/slack/interactivity");
        req1.Headers.Add("X-Slack-Request-Timestamp", ts1);
        req1.Headers.Add("X-Slack-Signature", sig1);
        req1.Content = new StringContent(formBody, Encoding.UTF8, "application/x-www-form-urlencoded");
        var resp1 = await client.SendAsync(req1);
        Assert.Equal(HttpStatusCode.OK, resp1.StatusCode);

        // Second click (same check-in, now PROCESSED)
        var ts2  = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var sig2 = Sign(formBody, ts2);
        using var req2 = new HttpRequestMessage(HttpMethod.Post, "/slack/interactivity");
        req2.Headers.Add("X-Slack-Request-Timestamp", ts2);
        req2.Headers.Add("X-Slack-Signature", sig2);
        req2.Content = new StringContent(formBody, Encoding.UTF8, "application/x-www-form-urlencoded");
        var resp2 = await client.SendAsync(req2);
        Assert.Equal(HttpStatusCode.OK, resp2.StatusCode);

        var body2 = await resp2.Content.ReadAsStringAsync();
        Assert.Contains("already answered", body2);
    }

    // ── Provenance: belief locator carries slack-user:{userId} ────────────────
    //
    // Regression guard for Part 5: the answeredBy parameter must flow all the way
    // into the written belief's Provenance.Locator.  A silent wiring break here
    // would lose the "who answered in Slack" trail with no test failure.

    [Fact]
    public async Task ValidRequest_BeliefProvenance_ContainsSlackUserId()
    {
        const string slackUserId = "U99999";
        var id = Guid.NewGuid();
        _fx.Store.Seed(OpenCheckIn(id));

        var formBody  = BuildSlackPayload(id, "true", slackUserId);
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var signature = Sign(formBody, timestamp);

        using var client = _fx.CreateClient();
        using var req    = new HttpRequestMessage(HttpMethod.Post, "/slack/interactivity");
        req.Headers.Add("X-Slack-Request-Timestamp", timestamp);
        req.Headers.Add("X-Slack-Signature", signature);
        req.Content = new StringContent(formBody, Encoding.UTF8, "application/x-www-form-urlencoded");

        var resp = await client.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        // Read all beliefs written for this vendor and assert the provenance locator
        // carries the Slack user identity.  Uses the same EntityStore that the endpoint's
        // VendorFileWriteService writes into (single shared in-memory SQLite instance).
        var beliefs = await _fx.EntityStore.GetBeliefHistoryAsync(VendorId);
        Assert.NotEmpty(beliefs);

        var slackBelief = beliefs.FirstOrDefault(b =>
            b.Provenance?.Locator?.Contains($"answered-by:slack-user:{slackUserId}") == true);
        Assert.NotNull(slackBelief);
    }
}
