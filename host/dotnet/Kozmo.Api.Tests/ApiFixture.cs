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

namespace Kozmo.Api.Tests;

[CollectionDefinition("ApiTests")]
public class ApiTestsCollection : ICollectionFixture<ApiFixture> { }

[CollectionDefinition("LiveSignalTests")]
public class LiveSignalTestsCollection : ICollectionFixture<LiveSignalFixture> { }

public sealed class ApiFixture : WebApplicationFactory<Program>, IAsyncLifetime
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureServices(services =>
        {
            var old = services.SingleOrDefault(d => d.ServiceType == typeof(IIiFacade));
            if (old != null) services.Remove(old);

            var store    = new SqliteEntityStore("Data Source=:memory:");
            var profile  = FindAndLoadProfile();
            var registry = BuildTestRegistry();

            services.AddSingleton(store);
            services.AddSingleton<IIiFacade>(new IiFacade(
                new ObservationModule(), new RubricModule(), new IndexModule(),
                new PostureModule(), new DecayEngine(),
                store, profile, registry, DemoClock.Fixed));

            // Live LLM factory: return null so /demo/live-signal returns 503 in baseline tests.
            var oldLlm = services.SingleOrDefault(d => d.ServiceType == typeof(Func<IKozmoLlm?>));
            if (oldLlm != null) services.Remove(oldLlm);
            services.AddSingleton<Func<IKozmoLlm?>>(() => null);
        });
    }

    async Task IAsyncLifetime.InitializeAsync()
    {
        var response = await CreateClient().PostAsync("/demo/reset", null);
        response.EnsureSuccessStatusCode();
    }

    Task IAsyncLifetime.DisposeAsync()
    {
        Dispose();
        return Task.CompletedTask;
    }

    private static SaasProfile FindAndLoadProfile()
    {
        var dir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(dir))
        {
            var candidate = Path.Combine(dir, "catalogue", "profiles", "saas");
            if (Directory.Exists(candidate)) return new Catalogue().Load(candidate);
            dir = Path.GetDirectoryName(dir);
        }
        throw new InvalidOperationException("Cannot locate catalogue/profiles/saas/");
    }

    internal static EntityRegistry BuildTestRegistry()
    {
        var reg = new EntityRegistry();
        reg.Register(Guid.Parse("eeeeeeee-0001-0000-0000-000000000001"), "Cloudwave Systems Inc.",
            new DateTimeOffset(2026, 9,  1, 0, 0, 0, TimeSpan.Zero));
        reg.Register(Guid.Parse("eeeeeeee-0002-0000-0000-000000000001"), "Corvus Infrastructure Ltd.",
            new DateTimeOffset(2026, 8, 15, 0, 0, 0, TimeSpan.Zero));
        reg.Register(Guid.Parse("eeeeeeee-0003-0000-0000-000000000001"), "Meridian IT Services Ltd.",
            new DateTimeOffset(2027, 1, 15, 0, 0, 0, TimeSpan.Zero));
        reg.Register(Guid.Parse("eeeeeeee-0004-0000-0000-000000000001"), "Helix Solutions AG",
            null);
        reg.Register(Guid.Parse("eeeeeeee-0005-0000-0000-000000000001"), "Northwind Logistics Inc.",
            null);
        reg.Register(Guid.Parse("eeeeeeee-0006-0000-0000-000000000001"), "Vertex Systems Ltd.",
            null);
        reg.Register(Guid.Parse("eeeeeeee-0007-0000-0000-000000000001"), "Aster Analytics Co.",
            null);
        reg.Register(Guid.Parse("eeeeeeee-0008-0000-0000-000000000001"), "Borealis Cloud GmbH",
            null);
        return reg;
    }

    internal static SaasProfile FindAndLoadProfileInternal()
    {
        var dir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(dir))
        {
            var candidate = Path.Combine(dir, "catalogue", "profiles", "saas");
            if (Directory.Exists(candidate)) return new Catalogue().Load(candidate);
            dir = Path.GetDirectoryName(dir);
        }
        throw new InvalidOperationException("Cannot locate catalogue/profiles/saas/");
    }
}

/// <summary>
/// Fixture for live-signal tests. Uses an in-memory store and a stub IKozmoLlm
/// that returns a canned classification — CI never calls OpenAI.
/// </summary>
public sealed class LiveSignalFixture : WebApplicationFactory<Program>, IAsyncLifetime
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureServices(services =>
        {
            var old = services.SingleOrDefault(d => d.ServiceType == typeof(IIiFacade));
            if (old != null) services.Remove(old);

            var store    = new SqliteEntityStore("Data Source=:memory:");
            var profile  = ApiFixture.FindAndLoadProfileInternal();
            var registry = ApiFixture.BuildTestRegistry();

            services.AddSingleton(store);
            services.AddSingleton<IIiFacade>(new IiFacade(
                new ObservationModule(), new RubricModule(), new IndexModule(),
                new PostureModule(), new DecayEngine(),
                store, profile, registry, DemoClock.Fixed));

            // Stub live LLM: returns an Operational/uptime_sla=0.10 classification.
            // Targeting Cloudwave (AtRisk) so a very low uptime_sla belief is meaningful.
            var oldLlm = services.SingleOrDefault(d => d.ServiceType == typeof(Func<IKozmoLlm?>));
            if (oldLlm != null) services.Remove(oldLlm);

            IKozmoLlm stub = new StubLlmClient();
            services.AddSingleton<Func<IKozmoLlm?>>(() => stub);
        });
    }

    async Task IAsyncLifetime.InitializeAsync()
    {
        var response = await CreateClient().PostAsync("/demo/reset", null);
        response.EnsureSuccessStatusCode();
    }

    Task IAsyncLifetime.DisposeAsync() { Dispose(); return Task.CompletedTask; }
}

/// <summary>
/// Stub LLM client used in CI tests. Returns a deterministic Operational/uptime_sla=0.10 classification.
/// Never makes a network call.
/// </summary>
internal sealed class StubLlmClient : IKozmoLlm
{
    private const string Response = """
        {"dimension":"Operational","criterion":"uptime_sla","value":0.10,"confidence":0.85,"reasoning":"Stub: critical uptime issue detected."}
        """;

    public Task<LlmResult> CompleteJsonAsync(string system, string user, int maxTokens = 500, CancellationToken ct = default)
    {
        var je = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(Response);
        return Task.FromResult(new LlmResult(je, Confidence: 0.85, ReasoningSummary: "Stub: critical uptime issue detected."));
    }
}
