using System.Net;
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
using Kozmo.Llm;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Wc.CheckIn;
using Wc.Contracts;
using Xunit;

namespace Kozmo.Api.Tests;

using CheckIn = global::Wc.Contracts.CheckIn;

// ── Collection ─────────────────────────────────────────────────────────────

[CollectionDefinition("ConfirmTests")]
public class ConfirmTestsCollection : ICollectionFixture<CheckInConfirmFixture> { }

// ── Fixture ─────────────────────────────────────────────────────────────────

/// <summary>
/// WebApplicationFactory for confirm-page tests.
/// Uses an in-memory store and a test secret so token generation is deterministic.
/// Overrides IIiFacade with a stub that never touches the network.
/// Separate from ApiFixture to avoid polluting the shared "ApiTests" store.
/// </summary>
public sealed class CheckInConfirmFixture : WebApplicationFactory<Program>, IAsyncLifetime
{
    internal const  string TestSecret  = "test-secret-32-bytes-of-entropy!";
    internal const  int    TtlDays     = 7;
    private  static readonly DateTimeOffset Anchor = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    internal InMemoryCheckInStoreForConfirm Store { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureServices(services =>
        {
            // Replace IIiFacade
            var oldFacade = services.SingleOrDefault(d => d.ServiceType == typeof(IIiFacade));
            if (oldFacade != null) services.Remove(oldFacade);

            var store    = new SqliteEntityStore("Data Source=:memory:");
            var profile  = ApiFixture.FindAndLoadProfileInternal();
            var registry = ApiFixture.BuildTestRegistry();

            services.AddSingleton(store);
            services.AddSingleton<IIiFacade>(new IiFacade(
                new ObservationModule(), new RubricModule(), new IndexModule(),
                new PostureModule(), new DecayEngine(),
                store, profile, registry, DemoClock.Fixed));

            // Replace ICheckInStore with our in-memory store
            var oldStore = services.SingleOrDefault(d => d.ServiceType == typeof(ICheckInStore));
            if (oldStore != null) services.Remove(oldStore);
            services.AddSingleton<ICheckInStore>(Store);

            // Replace CheckInTokenOptions with test options
            var oldOpts = services.SingleOrDefault(d => d.ServiceType == typeof(CheckInTokenOptions));
            if (oldOpts != null) services.Remove(oldOpts);
            services.AddSingleton(new CheckInTokenOptions(
                Secret:     TestSecret,
                TtlDays:    TtlDays,
                UiBaseUrl:  "http://localhost:3000",
                ApiBaseUrl: "http://localhost:5000"));

            // Kill live LLM
            var oldLlm = services.SingleOrDefault(d => d.ServiceType == typeof(Func<IKozmoLlm?>));
            if (oldLlm != null) services.Remove(oldLlm);
            services.AddSingleton<Func<IKozmoLlm?>>(() => null);

            // Replace SaasProfile for the page model
            var oldProfile = services.SingleOrDefault(d => d.ServiceType == typeof(SaasProfile));
            if (oldProfile != null) services.Remove(oldProfile);
            services.AddSingleton(profile);
        });
    }

    async Task IAsyncLifetime.InitializeAsync()
    {
        // Warm up the application (creates the DB schema, etc.)
        _ = CreateClient();
        await Task.CompletedTask;
    }

    Task IAsyncLifetime.DisposeAsync() { Dispose(); return Task.CompletedTask; }
}

/// <summary>Minimal in-memory ICheckInStore for the confirm-page fixture.</summary>
public sealed class InMemoryCheckInStoreForConfirm : ICheckInStore
{
    private readonly Dictionary<Guid, CheckIn> _store = new();

    public CheckIn? GetById(Guid id) => _store.TryGetValue(id, out var c) ? c : null;

    public void Seed(CheckIn ci) => _store[ci.CheckInId] = ci;

    public Task SaveAsync(CheckIn checkIn, CancellationToken ct = default)
    {
        _store[checkIn.CheckInId] = checkIn;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<CheckIn>> GetOpenAsync(CancellationToken ct = default)
    {
        IReadOnlyList<CheckIn> result = _store.Values
            .Where(c => c.Status == PendingStatus.OPEN).ToList();
        return Task.FromResult(result);
    }

    public Task<CheckIn?> GetAsync(Guid checkInId, CancellationToken ct = default)
        => Task.FromResult(_store.TryGetValue(checkInId, out var c) ? c : null);

    public Task<IReadOnlyList<CheckIn>> GetResolvedForVendorAsync(Guid vendorId, CancellationToken ct = default)
    {
        IReadOnlyList<CheckIn> result = _store.Values
            .Where(c => c.VendorId == vendorId &&
                        (c.Status == PendingStatus.PROCESSED || c.Status == PendingStatus.EXPIRED))
            .OrderBy(c => c.RaisedAt).ToList();
        return Task.FromResult(result);
    }
}

// ── Helpers ──────────────────────────────────────────────────────────────────

file static class ConfirmHelper
{
    public static CheckIn MakeOpenYesNo(Guid checkInId, Guid vendorId) => new CheckIn(
        CheckInId:     checkInId,
        VendorId:      vendorId,
        ProgramRunId:  Guid.NewGuid(),
        Kind:          CheckInKind.IDENTITY_CONFIRM,
        Question:      "Are Acme Corp and Acme Corporation the same vendor?",
        ResponseShape: ResponseShape.YES_NO,
        TargetField:   null,
        Owner:         "owner@test",
        Status:        PendingStatus.OPEN,
        RaisedAt:      DateTimeOffset.UtcNow,
        AnsweredAt:    null,
        ExpiresAt:     null,
        ResponseValue: null);

    /// <summary>
    /// Generates a token using the current UTC time so the token is valid when the page model
    /// validates it with DateTimeOffset.UtcNow.
    /// </summary>
    public static string MakeToken(Guid checkInId, string value) =>
        CheckInLinkToken.Generate(checkInId, value,
            CheckInConfirmFixture.TestSecret, CheckInConfirmFixture.TtlDays, DateTimeOffset.UtcNow);

    public static string ConfirmUrl(Guid checkInId, string token) =>
        $"/check-ins/{checkInId}/confirm?token={Uri.EscapeDataString(token)}";
}

// ── Tests ────────────────────────────────────────────────────────────────────

[Collection("ConfirmTests")]
public sealed class STests
{
    private static readonly Guid VendorId = new("EEEEEEEE-0000-0000-0000-000000000001");

    private readonly CheckInConfirmFixture _fx;

    public STests(CheckInConfirmFixture fx) => _fx = fx;

    private HttpClient Client => _fx.CreateClient();

    // ── S1. GET valid token → 200 with confirm form ────────────────────────

    [Fact]
    public async Task S1_Get_ValidToken_Returns200_WithConfirmForm()
    {
        var id    = Guid.NewGuid();
        var ci    = ConfirmHelper.MakeOpenYesNo(id, VendorId);
        _fx.Store.Seed(ci);

        var token = ConfirmHelper.MakeToken(id, "YES");
        var url   = ConfirmHelper.ConfirmUrl(id, token);

        var resp = await Client.GetAsync(url);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var html = await resp.Content.ReadAsStringAsync();
        Assert.Contains("Confirm your answer", html);
        Assert.Contains("Yes", html);
    }

    // ── S2. GET invalid token → 200 with error state ───────────────────────

    [Fact]
    public async Task S2_Get_InvalidToken_Returns200_WithErrorState()
    {
        var id  = Guid.NewGuid();
        var url = ConfirmHelper.ConfirmUrl(id, "this-is-not-a-valid-token");

        var resp = await Client.GetAsync(url);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var html = await resp.Content.ReadAsStringAsync();
        Assert.Contains("invalid or expired", html);
    }

    // ── S3. GET valid token, already-ANSWERED check-in → AlreadyRecorded ───

    [Fact]
    public async Task S3_Get_AlreadyAnswered_Returns200_AlreadyRecorded()
    {
        var id = Guid.NewGuid();
        var ci = ConfirmHelper.MakeOpenYesNo(id, VendorId) with
        {
            Status        = PendingStatus.ANSWERED,
            ResponseValue = "true"
        };
        _fx.Store.Seed(ci);

        var token = ConfirmHelper.MakeToken(id, "YES");
        var url   = ConfirmHelper.ConfirmUrl(id, token);

        var resp = await Client.GetAsync(url);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var html = await resp.Content.ReadAsStringAsync();
        Assert.Contains("Already recorded", html);
    }

    // ── S4. GET never calls AnswerCheckInService (invariant test) ──────────
    //
    // We verify this indirectly: after a GET on a valid token, the check-in
    // must still be OPEN in the store. If GET had called AnswerCheckInService,
    // the status would have changed.

    [Fact]
    public async Task S4_Get_NeverMutatesState_CheckInRemainsOpen()
    {
        var id  = Guid.NewGuid();
        var ci  = ConfirmHelper.MakeOpenYesNo(id, VendorId);
        _fx.Store.Seed(ci);

        var token = ConfirmHelper.MakeToken(id, "YES");
        var url   = ConfirmHelper.ConfirmUrl(id, token);

        // Multiple GETs — all must leave the check-in OPEN.
        await Client.GetAsync(url);
        await Client.GetAsync(url);
        await Client.GetAsync(url);

        var stored = _fx.Store.GetById(id);
        Assert.NotNull(stored);
        Assert.Equal(PendingStatus.OPEN, stored!.Status);
    }

    // ── S5. POST valid token → records answer, returns Recorded state ───────

    [Fact]
    public async Task S5_Post_ValidToken_RecordsAnswer_ReturnsRecorded()
    {
        var id  = Guid.NewGuid();
        var ci  = ConfirmHelper.MakeOpenYesNo(id, VendorId);
        _fx.Store.Seed(ci);

        var token = ConfirmHelper.MakeToken(id, "YES");
        var url   = ConfirmHelper.ConfirmUrl(id, token);

        var content = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("Id",    id.ToString()),
            new KeyValuePair<string, string>("Token", token)
        });

        var resp = await Client.PostAsync(url, content);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var html = await resp.Content.ReadAsStringAsync();
        Assert.Contains("Recorded", html);
    }

    // ── S6. POST idempotency — second POST returns AlreadyRecorded ──────────

    [Fact]
    public async Task S6_Post_SecondTime_ReturnsAlreadyRecorded()
    {
        var id  = Guid.NewGuid();
        var ci  = ConfirmHelper.MakeOpenYesNo(id, VendorId);
        _fx.Store.Seed(ci);

        var token   = ConfirmHelper.MakeToken(id, "NO");
        var url     = ConfirmHelper.ConfirmUrl(id, token);
        var content = () => new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("Id",    id.ToString()),
            new KeyValuePair<string, string>("Token", token)
        });

        var resp1 = await Client.PostAsync(url, content());
        Assert.Equal(HttpStatusCode.OK, resp1.StatusCode);
        Assert.Contains("Recorded", await resp1.Content.ReadAsStringAsync());

        var resp2 = await Client.PostAsync(url, content());
        Assert.Equal(HttpStatusCode.OK, resp2.StatusCode);
        var html2 = await resp2.Content.ReadAsStringAsync();
        Assert.Contains("Already recorded", html2);
    }

    // ── S7. POST with expired token → error state ────────────────────────────

    [Fact]
    public async Task S7_Post_ExpiredToken_ReturnsErrorState()
    {
        var id = Guid.NewGuid();
        var ci = ConfirmHelper.MakeOpenYesNo(id, VendorId);
        _fx.Store.Seed(ci);

        // Generate a token anchored far in the past → already expired.
        var pastNow  = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var expToken = CheckInLinkToken.Generate(id, "YES",
            CheckInConfirmFixture.TestSecret, 1, pastNow); // expires 2020-01-02 → long past
        var url = ConfirmHelper.ConfirmUrl(id, expToken);

        var content = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("Id",    id.ToString()),
            new KeyValuePair<string, string>("Token", expToken)
        });

        var resp = await Client.PostAsync(url, content);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var html = await resp.Content.ReadAsStringAsync();
        Assert.Contains("invalid or expired", html);

        // Check-in must still be OPEN — expired token must not record anything.
        var stored = _fx.Store.GetById(id);
        Assert.Equal(PendingStatus.OPEN, stored!.Status);
    }
}
