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
using Ii.Intake;
using Kozmo.Llm;
using Kozmo.Llm.OpenAi;
using Ig.Contracts;
using Ig.Resolution;
using Wc.CheckIn;
using Wc.Contracts;

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
var store         = new SqliteEntityStore($"Data Source={dbPath}", profile);
var registry      = BuildRegistry();
await LoadPersistedVendorsAsync(store, registry);
var liveCachePath = FindLlmCachePath(); // resolved once; used for both replay and live-classify
var facade        = BuildFacade(store, profile, registry, liveCachePath);
var sseHub        = new SseHub();
var checkInRepo   = new CheckInRepository(store);

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
builder.Services.AddSingleton<ICheckInRowStore>(store);
builder.Services.AddSingleton<ICheckInStore>(checkInRepo);
builder.Services.AddSingleton<ICheckInTransport>(new InAppCheckInTransport());

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

// GET /vendors — all known vendors (seeded + user-created)
app.MapGet("/vendors", async (IIiFacade f, EntityRegistry reg, SaasProfile prof) =>
{
    var now     = DemoClock.AsOf;
    var vendors = new List<VendorSummaryDto>();
    foreach (var id in reg.GetAllIds())
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
    if (string.IsNullOrWhiteSpace(request.Body))
        return Results.BadRequest(new { error = "Body must not be empty." });

    var bodyPayload = new Dictionary<string, object?> { ["body"] = request.Body };
    var entityGuid  = reg.Resolve(Guid.Empty, bodyPayload, prof);
    if (entityGuid == Guid.Empty)
        return Results.UnprocessableEntity(new { error = "Cannot identify vendor — mention Cloudwave, Corvus, or Meridian by name." });

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
              data = new { vendorId = entityGuid.ToString(), vendorName = entity.CanonicalName } }, JsonOpts));

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
                  data = new { vendorId = entityGuid.ToString(), classification = classificationView,
                               vendor = vendorDto, index = indexDto } }, JsonOpts));

        return Results.Ok(new LiveSignalResponse(classificationView, vendorDto, indexDto));
    }
    catch (Exception ex)
    {
        hub.Broadcast(JsonSerializer.Serialize(
            new { type = "live-error", ts = DateTimeOffset.UtcNow,
                  data = new { vendorId = entityGuid.ToString(), error = ex.Message } }, JsonOpts));
        return Results.Problem(detail: ex.Message, statusCode: 502);
    }
});

// POST /vendors/resolve-name — exact-name identity upsert
// Exact OrdinalIgnoreCase match → returns existing vendorId (isNew=false).
// No match → mints new GUID, registers in-memory, persists to DB (isNew=true).
app.MapPost("/vendors/resolve-name", async (
    NameResolveRequest request,
    EntityRegistry     reg,
    SqliteEntityStore  storeInst) =>
{
    if (string.IsNullOrWhiteSpace(request.VendorName))
        return Results.BadRequest(new { error = "vendorName must not be empty." });

    var (vendorId, isNew) = reg.Upsert(request.VendorName.Trim());

    if (isNew)
        await storeInst.SaveVendorAsync(vendorId, request.VendorName.Trim(), null, DemoClock.AsOf);

    var canonical = reg.GetEntity(vendorId)!.CanonicalName;
    return Results.Ok(new NameResolveResponse(vendorId.ToString(), isNew, canonical));
});

// POST /vendors/{id}/vendor-file/ingest — run vendor file pipeline for a fixture path
app.MapPost("/vendors/{id}/vendor-file/ingest", async (
    string             id,
    VendorFileIngestRequest request,
    IIiFacade          f,
    EntityRegistry     reg,
    SaasProfile        prof,
    SqliteEntityStore  storeInst) =>
{
    if (!Guid.TryParse(id, out var guid)) return Results.BadRequest("Invalid GUID");
    if (reg.GetEntity(guid) is null) return Results.NotFound();
    if (string.IsNullOrWhiteSpace(request.FixturePath))
        return Results.BadRequest(new { error = "fixturePath must not be empty." });

    var vendorName = reg.GetEntity(guid)?.CanonicalName ?? guid.ToString();
    var outputPath = Path.Combine(Path.GetTempPath(), $"{guid:N}.vendor.md");
    var runner = new VendorFileStageRunner(storeInst, prof, f);
    var result = await runner.RunAsync(guid, vendorName, DemoClock.AsOf, request.FixturePath, outputPath);

    return Results.Ok(new
    {
        vendorId     = guid,
        completeness = new { ratio = result.Completeness.Ratio, gaps = result.Completeness.GapKeys },
        evidenceCount = result.Evidence.Count,
        claimsWritten = result.Beliefs.Count,
        band          = result.Index?.Band.ToString(),
        stance        = result.Posture?.Stance.ToString(),
        fingerprint   = result.Index?.Fingerprint
    });
});

// POST /vendors/vendor-file/upload-contract — accept a real PDF, extract PRIMARY beliefs, recompute
app.MapPost("/vendors/vendor-file/upload-contract", async (
    HttpContext       ctx,
    IIiFacade         f,
    EntityRegistry    reg,
    SaasProfile       prof,
    SqliteEntityStore storeInst,
    Func<IKozmoLlm?>  liveLlmFactory) =>
{
    IFormCollection form;
    try   { form = await ctx.Request.ReadFormAsync(); }
    catch { return Results.BadRequest(new { error = "Expected multipart/form-data." }); }

    var file         = form.Files.GetFile("file");
    var vendorNameRaw = form["vendorName"].FirstOrDefault()?.Trim();

    if (file is null || file.Length == 0)
        return Results.BadRequest(new { error = "file is required." });
    if (string.IsNullOrEmpty(vendorNameRaw))
        return Results.BadRequest(new { error = "vendorName is required." });

    var liveLlm = liveLlmFactory();
    if (liveLlm is null)
        return Results.Problem(
            detail:     "OPENAI_API_KEY is not set or fixture cache path is unavailable.",
            statusCode: 503);

    // Identity upsert — exact-name match, or mint a fresh GUID
    var (vendorId, isNew) = reg.Upsert(vendorNameRaw);
    if (isNew)
        await storeInst.SaveVendorAsync(vendorId, vendorNameRaw, null, DemoClock.AsOf);

    // Read PDF bytes
    using var ms = new MemoryStream((int)file.Length);
    await file.CopyToAsync(ms);
    var pdfBytes = ms.ToArray();

    // Append Evidence row (SignedContract → PRIMARY)
    var ev = new Evidence(
        EvidenceId: Guid.NewGuid(),
        VendorId:   vendorId,
        DocType:    DocType.SignedContract,
        SourceTier: SourceTier.Primary,
        Ref:        file.FileName,
        DocVersion: 1,
        IngestedAt: DemoClock.AsOf);
    await storeInst.AppendEvidenceAsync(ev);

    // PdfTextExtractor → VendorFilePdfLane → write PRIMARY beliefs
    var writeService = new VendorFileWriteService(storeInst, prof);
    var lane         = new VendorFilePdfLane(liveLlm, writeService, prof);
    var pageTexts    = new PdfTextExtractor().ExtractPageTexts(pdfBytes);
    await lane.ExtractAndWriteAsync(vendorId, ev, pageTexts, DemoClock.AsOf);

    // Recompute posture so the vendor-file Razor page sees fresh state on arrival
    await f.RecomputeVendorAsync(vendorId);

    return Results.Ok(new { vendorId = vendorId.ToString() });
});

// GET /vendors/{id}/vendor-file/markdown — render the vendor file as raw markdown text
app.MapGet("/vendors/{id}/vendor-file/markdown", async (
    string            id,
    IIiFacade         f,
    EntityRegistry    reg,
    SqliteEntityStore storeInst) =>
{
    if (!Guid.TryParse(id, out var guid)) return Results.BadRequest("Invalid GUID");
    var entity = reg.GetEntity(guid);
    if (entity is null) return Results.NotFound();

    var judgement     = await f.RecomputeVendorAsync(guid);
    var activeBeliefs = await storeInst.GetCurrentBeliefsAsync(guid);
    var allBeliefs    = await storeInst.GetBeliefHistoryAsync(guid);
    var evidence      = await storeInst.GetEvidenceForVendorAsync(guid);

    var markdown = VendorFileRenderer.Render(
        vendorId:      guid,
        vendorName:    entity.CanonicalName,
        asOf:          DemoClock.AsOf,
        judgement:     judgement,
        activeBeliefs: activeBeliefs,
        allBeliefs:    allBeliefs,
        evidence:      evidence);

    return Results.Content(markdown, "text/plain; charset=utf-8");
});

// GET /vendors/{id}/vendor-file — summary of vendor file state
app.MapGet("/vendors/{id}/vendor-file", async (
    string            id,
    IIiFacade         f,
    EntityRegistry    reg,
    SaasProfile       prof,
    SqliteEntityStore storeInst) =>
{
    if (!Guid.TryParse(id, out var guid)) return Results.BadRequest("Invalid GUID");
    if (reg.GetEntity(guid) is null) return Results.NotFound();

    var evidence = await storeInst.GetEvidenceForVendorAsync(guid);
    var beliefs  = await storeInst.GetCurrentBeliefsAsync(guid);
    var vfBeliefs = beliefs.Where(b => !string.IsNullOrEmpty(b.ClaimKey)).ToList();
    var comp = new Km.Store.CompletenessService(prof).Compute(guid, vfBeliefs);

    return Results.Ok(new
    {
        vendorId     = guid,
        completeness = new { ratio = comp.Ratio, filledKeys = comp.FilledKeys, gapKeys = comp.GapKeys },
        evidenceCount = evidence.Count,
        claimsCount   = vfBeliefs.Count
    });
});

// GET /checkins — list all OPEN check-ins for the in-app pending view
app.MapGet("/checkins", async (ICheckInStore checkInStore) =>
{
    var open = await checkInStore.GetOpenAsync();
    return Results.Ok(open.Select(ci => new
    {
        checkInId     = ci.CheckInId,
        vendorId      = ci.VendorId,
        kind          = ci.Kind.ToString(),
        question      = ci.Question,
        responseShape = ci.ResponseShape.ToString(),
        targetField   = ci.TargetField,
        raisedAt      = ci.RaisedAt
    }).ToList());
});

// POST /checkins/{id}/answer — record a structured human response then process inline (Commit 3)
app.MapPost("/checkins/{id}/answer", async (
    string               id,
    CheckInAnswerRequest req,
    ICheckInStore        checkInStore,
    IIiFacade            f,
    SaasProfile          profile,
    SqliteEntityStore    storeInst) =>
{
    if (!Guid.TryParse(id, out var guid))
        return Results.BadRequest(new { error = "Invalid check-in ID." });

    var answerSvc = new AnswerCheckInService();
    var answer    = await answerSvc.AnswerAsync(guid, req.ResponseValue ?? string.Empty, DateTimeOffset.UtcNow, checkInStore);

    if (answer.Outcome != AnswerOutcome.Ok)
        return answer.Outcome switch
        {
            AnswerOutcome.NotFound        => Results.NotFound(new { error = "Check-in not found." }),
            AnswerOutcome.AlreadyAnswered => Results.Conflict(new { error = "Check-in is already answered." }),
            AnswerOutcome.ShapeMismatch   => Results.BadRequest(new { error = "Response value does not match the expected shape." }),
            _                             => Results.Problem("Unexpected outcome.")
        };

    // Process the now-ANSWERED check-in inline.
    // Real IdentityRegistry backed by SqliteEntityStore (which implements IRegistryStore).
    // IDENTITY_CONFIRM merge is correct in unit tests (registry pre-populated by the test).
    // Live path: end-to-end-inert pending Phase 4 — the KYV resolution pipeline (Stage F)
    // must persist CanonicalVendor rows into registry_vendors before a merge fires in production.
    // DIMENSION_GAP and the wrong-match guard are unaffected by this gap.
    var processSvc = new ProcessCheckInService();
    var writeSvc   = new VendorFileWriteService(storeInst, profile);
    await processSvc.ProcessAsync(
        guid, checkInStore, new IdentityRegistry(storeInst), writeSvc, f, profile,
        DateTimeOffset.UtcNow);

    return Results.Ok(new { outcome = "Ok", checkInId = answer.Updated!.CheckInId });
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

static async Task LoadPersistedVendorsAsync(SqliteEntityStore store, EntityRegistry registry)
{
    var persisted = await store.LoadVendorsAsync();
    foreach (var (id, name, renewalDate) in persisted)
        registry.Register(id, name, renewalDate);
}

static EntityRegistry BuildRegistry()
{
    var reg = new EntityRegistry();
    reg.Register(SeedData.CloudwaveId, "Cloudwave Systems Inc.",
        new DateTimeOffset(2026, 9,  1, 0, 0, 0, TimeSpan.Zero));
    reg.Register(SeedData.CorvusId,    "Corvus Infrastructure Ltd.",
        new DateTimeOffset(2026, 8, 15, 0, 0, 0, TimeSpan.Zero));
    reg.Register(SeedData.MeridianId, "Meridian IT Services Ltd.",
        new DateTimeOffset(2027, 1, 15, 0, 0, 0, TimeSpan.Zero));
    reg.Register(SeedData.HelixId, "Helix Systems Ltd.");
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

record CheckInAnswerRequest(string? ResponseValue);
