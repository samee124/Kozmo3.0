using Ii.Contracts;
using Ii.Decay;
using Ii.Index;
using Ii.Observation;
using Ii.Posture;
using Ii.Rubric;
using Ii.Spine;
using Km.Store;
using Kozmo.Contracts.Config;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Kozmo.Api.Tests;

[CollectionDefinition("ApiTests")]
public class ApiTestsCollection : ICollectionFixture<ApiFixture> { }

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

            services.AddSingleton<IIiFacade>(new IiFacade(
                new ObservationModule(), new RubricModule(), new IndexModule(),
                new PostureModule(), new DecayEngine(),
                store, profile, registry, DemoClock.Fixed));
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

    private static EntityRegistry BuildTestRegistry()
    {
        var reg = new EntityRegistry();
        reg.Register(Guid.Parse("eeeeeeee-0001-0000-0000-000000000001"), "Cloudwave Systems Inc.",
            new DateTimeOffset(2026, 9,  1, 0, 0, 0, TimeSpan.Zero));
        reg.Register(Guid.Parse("eeeeeeee-0002-0000-0000-000000000001"), "Corvus Infrastructure Ltd.",
            new DateTimeOffset(2026, 8, 15, 0, 0, 0, TimeSpan.Zero));
        reg.Register(Guid.Parse("eeeeeeee-0003-0000-0000-000000000001"), "Meridian IT Services Ltd.",
            new DateTimeOffset(2027, 1, 15, 0, 0, 0, TimeSpan.Zero));
        return reg;
    }
}
