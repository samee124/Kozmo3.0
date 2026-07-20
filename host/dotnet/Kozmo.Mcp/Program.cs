using Ii.Decay;
using Ii.Index;
using Ii.Observation;
using Ii.Posture;
using Ii.Rubric;
using Ii.Spine;
using Km.Store;
using Kozmo.Llm;
using ModelContextProtocol.AspNetCore;
using Rm.Contracts;
using Kozmo.Mcp;
using Rm.Query;
using Wc.CheckIn;

var builder = WebApplication.CreateBuilder(args);

// ── Compose the query stack ────────────────────────────────────────────────

var catalogueDir = FindCatalogueDir();
var profile      = new Catalogue().Load(catalogueDir);

var dbPath = builder.Configuration["Db:Path"]
             ?? Environment.GetEnvironmentVariable("KOZMO_DB_PATH")
             ?? Path.Combine(AppContext.BaseDirectory, "kozmo-demo.db");
var store    = new SqliteEntityStore($"Data Source={dbPath}", profile);
var registry = BuildRegistry();
await LoadPersistedVendorsAsync(store, registry);

// LLM phrasing (optional — falls back to deterministic template when absent)
var llmCachePath = FindLlmCachePath();
IKozmoLlm? llm = null;
if (llmCachePath is not null)
{
    var info = new FileInfo(llmCachePath);
    if (info.Length > 4)
        llm = new CachingLlmClient(llmCachePath, recordMode: false);
}

// IiFacade: use DemoClock.Fixed so decay is evaluated at the same frozen instant as the Api
var facade = new IiFacade(
    new ObservationModule(llm),
    new RubricModule(),
    new IndexModule(),
    new PostureModule(),
    new DecayEngine(),
    store,
    profile,
    registry,
    DemoClock.Fixed);

var checkInRepo    = new CheckInRepository(store);
var vendorQuerySvc = new VendorQueryService(facade, checkInRepo, registry, llm);

// ── DI ────────────────────────────────────────────────────────────────────

builder.Services.AddSingleton<IVendorQueryService>(vendorQuerySvc);

builder.Services.AddMcpServer()
    .WithHttpTransport(o => o.Stateless = true)
    .WithToolsFromAssembly();

// ── Build & run ───────────────────────────────────────────────────────────

var app = builder.Build();

app.UseMiddleware<McpAuthMiddleware>();
app.MapMcp("/mcp");

app.Run();

// ── Helpers ───────────────────────────────────────────────────────────────

static string FindCatalogueDir()
{
    var dir = AppContext.BaseDirectory;
    while (!string.IsNullOrEmpty(dir))
    {
        var candidate = Path.Combine(dir, "catalogue", "profiles", "saas");
        if (Directory.Exists(candidate)) return candidate;
        dir = Path.GetDirectoryName(dir);
    }
    throw new InvalidOperationException(
        "Cannot locate catalogue/profiles/saas/. Run from the repo root or a subdirectory.");
}

static string? FindLlmCachePath()
{
    var dir = AppContext.BaseDirectory;
    while (!string.IsNullOrEmpty(dir))
    {
        var candidate = Path.Combine(dir, "fixtures", "llm-cache.json");
        if (File.Exists(candidate)) return candidate;
        dir = Path.GetDirectoryName(dir);
    }
    return null;
}

static EntityRegistry BuildRegistry()
{
    var reg = new EntityRegistry();
    reg.Register(Guid.Parse("eeeeeeee-0001-0000-0000-000000000001"), "Cloudwave Systems Inc.",
        new DateTimeOffset(2026, 9,  1, 0, 0, 0, TimeSpan.Zero));
    reg.Register(Guid.Parse("eeeeeeee-0002-0000-0000-000000000001"), "Corvus Infrastructure Ltd.",
        new DateTimeOffset(2026, 8, 15, 0, 0, 0, TimeSpan.Zero));
    reg.Register(Guid.Parse("eeeeeeee-0003-0000-0000-000000000001"), "Meridian IT Services Ltd.",
        new DateTimeOffset(2027, 1, 15, 0, 0, 0, TimeSpan.Zero));
    reg.Register(Guid.Parse("eeeeeeee-0004-0000-0000-000000000001"), "Helix Systems Ltd.");
    reg.Register(Guid.Parse("eeeeeeee-0005-0000-0000-000000000001"), "Northwind Systems");
    reg.Register(Guid.Parse("eeeeeeee-0006-0000-0000-000000000001"), "Ridgeline Software");
    return reg;
}

static async Task LoadPersistedVendorsAsync(SqliteEntityStore store, EntityRegistry registry)
{
    var persisted = await store.LoadVendorsAsync();
    foreach (var (id, name, renewalDate) in persisted)
        registry.Register(id, name, renewalDate);

    var kyvVendors = await store.LoadAllKyvVendorsAsync();
    foreach (var (id, name, renewalDate) in kyvVendors)
        registry.Register(id, name, renewalDate);
}
