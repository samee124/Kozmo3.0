using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Ii.Contracts;
using Ii.Decay;
using Ii.Index;
using Ii.Observation;
using Ii.Posture;
using Ii.Rubric;
using Ii.Spine;
using Km.Store;
using Kozmo.Api;
using Kozmo.Contracts;
using Kozmo.Contracts.Config;
using Kozmo.Contracts.Interfaces;
using Kozmo.Llm;
using Kozmo.Llm.OpenAi;

// ── JSON options (shared by SSE serialisation) ─────────────────────────────

var JsonOpts = new JsonSerializerOptions
{
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
};

// ── Compose the demo stack ─────────────────────────────────────────────────

var catalogueDir  = FindCatalogueDir();
var profile       = new Catalogue().Load(catalogueDir);
var dbPath        = Path.Combine(AppContext.BaseDirectory, "kozmo-demo.db");
var store         = new SqliteEntityStore($"Data Source={dbPath}");
var registry      = BuildRegistry();
var liveCachePath = FindLlmCachePath(); // resolved once; used for both replay and live-classify
var facade        = BuildFacade(store, profile, registry, liveCachePath);
var sseHub        = new SseHub();

// ── ASP.NET Core setup ────────────────────────────────────────────────────

var builder = WebApplication.CreateBuilder(args);

builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    o.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
});

builder.Services.AddRazorPages();

builder.Services.AddCors(c =>
    c.AddDefaultPolicy(p => p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));

builder.Services.AddSingleton<IIiFacade>(facade);
builder.Services.AddSingleton(profile);
builder.Services.AddSingleton(registry);
builder.Services.AddSingleton(sseHub);
builder.Services.AddSingleton(store);

// Live LLM factory: record-mode CachingLlmClient wrapping real OpenAiLlmClient.
// Returns null when OPENAI_API_KEY is absent or fixture cache path unavailable → endpoint returns 503.
builder.Services.AddSingleton<Func<IKozmoLlm?>>(sp => () =>
{
    if (liveCachePath == null) return null;
    try { return new CachingLlmClient(liveCachePath, recordMode: true, inner: new OpenAiLlmClient()); }
    catch (InvalidOperationException) { return null; } // OPENAI_API_KEY not set
});

var app = builder.Build();
app.UseStaticFiles();
app.UseCors();
app.MapRazorPages();

// ── Seed on first start ───────────────────────────────────────────────────

await SeedIfEmptyAsync(facade, store);

// ── Endpoints ─────────────────────────────────────────────────────────────

// GET /vendors — list all three vendors
app.MapGet("/vendors", async (IIiFacade f, EntityRegistry reg, SaasProfile prof) =>
{
    var now     = DemoClock.AsOf;
    var vendors = new List<VendorSummaryDto>();
    foreach (var id in SeedData.VendorIds)
    {
        var entity = reg.GetEntity(id);
        if (entity is null) continue;
        var idx  = await f.GetIndexAsync(id);
        var pos  = await f.GetPostureAsync(id);
        vendors.Add(DtoMapper.ToSummary(id, entity, idx, pos, now));
    }
    return Results.Ok(vendors);
});

// GET /vendors/{id} — detail for one vendor
app.MapGet("/vendors/{id}", async (string id, IIiFacade f, EntityRegistry reg, SaasProfile prof) =>
{
    if (!Guid.TryParse(id, out var guid)) return Results.BadRequest("Invalid GUID");
    var entity = reg.GetEntity(guid);
    if (entity is null) return Results.NotFound();
    var idx  = await f.GetIndexAsync(guid);
    var pos  = await f.GetPostureAsync(guid);
    if (idx is null || pos is null) return Results.NotFound();
    return Results.Ok(DtoMapper.ToDetail(guid, entity, idx, pos, prof, DemoClock.AsOf));
});

// GET /vendors/{id}/trail — full glass-box reasoning chain
app.MapGet("/vendors/{id}/trail", async (string id, IIiFacade f, EntityRegistry reg, SaasProfile prof) =>
{
    if (!Guid.TryParse(id, out var guid)) return Results.BadRequest("Invalid GUID");
    var entity = reg.GetEntity(guid);
    if (entity is null) return Results.NotFound();
    var trail = await f.GetReasoningTrailAsync(guid);
    if (trail?.Index is null || trail.Posture is null) return Results.NotFound();
    return Results.Ok(DtoMapper.ToTrail(
        trail.Index, trail.Posture, entity,
        trail.CurrentBeliefs, trail.SourceSignals, prof, DemoClock.AsOf));
});

// GET /vendors/{id}/trajectory — ordered history for chart
app.MapGet("/vendors/{id}/trajectory", async (string id, IIiFacade f, EntityRegistry reg) =>
{
    if (!Guid.TryParse(id, out var guid)) return Results.BadRequest("Invalid GUID");
    if (reg.GetEntity(guid) is null) return Results.NotFound();
    var points = await f.GetTrajectoryAsync(guid);
    return Results.Ok(points.Select(DtoMapper.ToTrajectoryPoint).ToList());
});

// GET /events — SSE stream for replay
app.MapGet("/events", async (SseHub hub, HttpContext ctx) =>
{
    ctx.Response.Headers.ContentType  = "text/event-stream";
    ctx.Response.Headers.CacheControl = "no-cache";
    ctx.Response.Headers["X-Accel-Buffering"] = "no";
    await ctx.Response.Body.FlushAsync();

    try
    {
        await foreach (var msg in hub.Subscribe(ctx.RequestAborted))
        {
            await ctx.Response.WriteAsync($"data: {msg}\n\n");
            await ctx.Response.Body.FlushAsync();
        }
    }
    catch (OperationCanceledException) { /* client disconnected */ }
});

// POST /demo/reset — re-seed from scratch, return new VendorSummary[]
app.MapPost("/demo/reset", async (IIiFacade f, EntityRegistry reg, SaasProfile prof, SseHub hub) =>
{
    await f.ResetAsync();
    foreach (var sig in SeedData.AllSignals)
        await f.SubmitSignalAsync(sig);

    var vendors = await CollectVendorSummariesAsync(f, reg, prof, DemoClock.AsOf);

    hub.Broadcast(JsonSerializer.Serialize(
        new { type = "reset-complete", ts = DateTimeOffset.UtcNow, data = new { vendors } }, JsonOpts));

    return Results.Ok(new { vendors });
});

// POST /demo/replay — stepped replay over SSE, returns 202 immediately
app.MapPost("/demo/replay", (IIiFacade f, EntityRegistry reg, SaasProfile prof, SseHub hub) =>
{
    _ = Task.Run(async () =>
    {
        await f.ResetAsync();

        foreach (var sig in SeedData.AllSignals)
        {
            await f.SubmitSignalAsync(sig);

            var idx    = await f.GetIndexAsync(sig.EntityId);
            var posture = await f.GetPostureAsync(sig.EntityId);
            var entity  = reg.GetEntity(sig.EntityId);

            if (idx is not null && posture is not null && entity is not null)
            {
                var stepData = new
                {
                    entityId    = sig.EntityId.ToString(),
                    signalId    = sig.Id.ToString(),
                    timestamp   = sig.ObservedAt,
                    index       = DtoMapper.ToIndexView(idx, prof),
                    stance      = posture.Stance.ToString(),
                    fingerprint = idx.Fingerprint
                };
                hub.Broadcast(JsonSerializer.Serialize(
                    new { type = "replay-step", ts = DateTimeOffset.UtcNow, data = stepData }, JsonOpts));
            }

            await Task.Delay(400); // pacing for live demo
        }

        var vendors = await CollectVendorSummariesAsync(f, reg, prof, DemoClock.AsOf);
        hub.Broadcast(JsonSerializer.Serialize(
            new { type = "replay-complete", ts = DateTimeOffset.UtcNow, data = new { vendors } }, JsonOpts));
    });

    return Results.Accepted();
});

// POST /demo/live-signal — classify free text with live OpenAI, run through deterministic engine
// This is the ONE endpoint that makes a live network call; the I&I modules never touch the real client.
app.MapPost("/demo/live-signal", async (
    LiveSignalRequest        request,
    IIiFacade                f,
    SqliteEntityStore        storeInst,
    EntityRegistry           reg,
    SaasProfile              prof,
    SseHub                   hub,
    Func<IKozmoLlm?>         liveLlmFactory) =>
{
    if (!Guid.TryParse(request.VendorId, out var entityGuid))
        return Results.BadRequest(new { error = "Invalid vendorId." });

    if (string.IsNullOrWhiteSpace(request.Body))
        return Results.BadRequest(new { error = "Body must not be empty." });

    var entity = reg.GetEntity(entityGuid);
    if (entity is null) return Results.NotFound(new { error = "Vendor not found." });

    // Resolve live LLM — absent = no API key or no cache path
    var liveLlm = liveLlmFactory();
    if (liveLlm is null)
        return Results.Problem(
            detail:     "OPENAI_API_KEY is not set or fixture cache path is unavailable.",
            statusCode: 503);

    hub.Broadcast(JsonSerializer.Serialize(
        new { type = "live-classifying", ts = DateTimeOffset.UtcNow,
              data = new { vendorId = request.VendorId, vendorName = entity.CanonicalName } }, JsonOpts));

    try
    {
        // Build a temporary live spine that shares the runtime store but uses the live LLM.
        // Modules are stateless pure functions — safe to instantiate per-request.
        var liveObs   = new ObservationModule(liveLlm);
        var liveSpine = new IiFacade(liveObs, new RubricModule(), new IndexModule(),
                                     new PostureModule(), new DecayEngine(),
                                     storeInst, prof, reg, DemoClock.Fixed);

        var traceId = Guid.NewGuid();
        var signal  = new Signal(
            Id:           Guid.NewGuid(),
            EntityId:     entityGuid,
            CustomerId:   SeedData.CustomerId,
            SourceSystem: SourceSystem.HumanReport,
            ExternalId:   $"live-{traceId:N}",
            Payload:      new Dictionary<string, object?> { ["body"] = request.Body },
            ObservedAt:   DemoClock.AsOf,
            ReceivedAt:   DemoClock.AsOf,
            TraceId:      traceId);

        // Pre-classify to capture classification metadata for the response.
        // Record-mode CachingLlmClient calls OpenAI once, writes to cache; the subsequent
        // SubmitSignalAsync call hits the cache and never calls OpenAI again.
        var cl = liveObs.Classify(signal, prof);
        if (cl is null)
            return Results.UnprocessableEntity(new { error = "Model could not classify the signal." });

        var classificationView = new ClassificationView(
            Dimension:        cl.Dimension.ToString(),
            Criterion:        cl.Criterion,
            Value:            cl.Value,
            MethodConfidence: cl.MethodConfidence ?? 0.0,
            ReasoningSummary: cl.ReasoningSummary ?? "",
            SourceTier:       (signal.SourceSystem == SourceSystem.HumanReport
                                    ? SourceTier.Reported : SourceTier.Inferred).ToString());

        // Full pipeline: belief created, index recomputed, posture assigned, written to store
        await liveSpine.SubmitSignalAsync(signal);

        var idx     = await f.GetIndexAsync(entityGuid);
        var posture = await f.GetPostureAsync(entityGuid);
        if (idx is null || posture is null)
            return Results.Problem("Engine state unavailable after submission.", statusCode: 500);

        var vendorDto = DtoMapper.ToSummary(entityGuid, entity, idx, posture, DemoClock.AsOf);
        var indexDto  = DtoMapper.ToIndexView(idx, prof);

        hub.Broadcast(JsonSerializer.Serialize(
            new { type = "live-update", ts = DateTimeOffset.UtcNow,
                  data = new { vendorId = request.VendorId, classification = classificationView,
                               vendor = vendorDto, index = indexDto } }, JsonOpts));

        return Results.Ok(new LiveSignalResponse(classificationView, vendorDto, indexDto));
    }
    catch (Exception ex)
    {
        hub.Broadcast(JsonSerializer.Serialize(
            new { type = "live-error", ts = DateTimeOffset.UtcNow,
                  data = new { vendorId = request.VendorId, error = ex.Message } }, JsonOpts));
        return Results.Problem(detail: ex.Message, statusCode: 502);
    }
});

app.Run();

// ── Local helpers ─────────────────────────────────────────────────────────

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
        "Cannot locate catalogue/profiles/saas/ directory. Run from the repo root or a subdirectory.");
}

static EntityRegistry BuildRegistry()
{
    var reg = new EntityRegistry();
    reg.Register(SeedData.CloudwaveId, "Cloudwave Systems Inc.",
        new DateTimeOffset(2026, 9,  1, 0, 0, 0, TimeSpan.Zero));
    reg.Register(SeedData.CorvusId,    "Corvus Infrastructure Ltd.",
        new DateTimeOffset(2026, 8, 15, 0, 0, 0, TimeSpan.Zero));
    reg.Register(SeedData.MeridianId,  "Meridian IT Services Ltd.",
        new DateTimeOffset(2027, 1, 15, 0, 0, 0, TimeSpan.Zero));
    return reg;
}

static IiFacade BuildFacade(IEntityStore store, SaasProfile profile, EntityRegistry registry, string? cachePath)
{
    // Wire CachingLlmClient (replay) if llm-cache.json exists and is non-empty.
    // Before seed-prep runs the cache is empty ({}) — LLM is null and free-text signals are silently skipped.
    IKozmoLlm? llm = null;
    if (cachePath != null)
    {
        var info = new FileInfo(cachePath);
        if (info.Length > 4) // more than just "{}" or "{}\n"
            llm = new CachingLlmClient(cachePath, recordMode: false);
    }

    return new IiFacade(
        new ObservationModule(llm),
        new RubricModule(),
        new IndexModule(),
        new PostureModule(),
        new DecayEngine(),
        store,
        profile,
        registry,
        DemoClock.Fixed);
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

static async Task SeedIfEmptyAsync(IIiFacade facade, SqliteEntityStore store)
{
    var any = await store.GetIndexAsync(SeedData.VendorIds[0]);
    if (any is not null) return; // already seeded

    foreach (var sig in SeedData.AllSignals)
        await facade.SubmitSignalAsync(sig);
}

static async Task<List<VendorSummaryDto>> CollectVendorSummariesAsync(
    IIiFacade facade, EntityRegistry reg, SaasProfile prof, DateTimeOffset asOf)
{
    var vendors = new List<VendorSummaryDto>();
    foreach (var id in SeedData.VendorIds)
    {
        var entity = reg.GetEntity(id);
        if (entity is null) continue;
        var idx = await facade.GetIndexAsync(id);
        var pos = await facade.GetPostureAsync(id);
        vendors.Add(DtoMapper.ToSummary(id, entity, idx, pos, asOf));
    }
    return vendors;
}

// ── SseHub — multi-client broadcast ───────────────────────────────────────

public sealed class SseHub
{
    private readonly object _lock = new();
    private readonly List<ChannelWriter<string>> _clients = [];

    public IAsyncEnumerable<string> Subscribe(CancellationToken ct)
    {
        var channel = Channel.CreateUnbounded<string>();
        lock (_lock) { _clients.Add(channel.Writer); }
        ct.Register(() =>
        {
            lock (_lock) { _clients.Remove(channel.Writer); }
            channel.Writer.TryComplete();
        });
        return channel.Reader.ReadAllAsync(ct);
    }

    public void Broadcast(string json)
    {
        lock (_lock)
        {
            foreach (var w in _clients)
                w.TryWrite(json);
        }
    }
}

// Required for WebApplicationFactory<Program> in Kozmo.Api.Tests
public partial class Program { }
