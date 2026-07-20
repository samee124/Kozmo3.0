using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Ii.Completeness;
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
using Kozmo.Connector.GoogleDrive;
using Kyv.ProgramRunner;
using If.MicrosoftGraph;
using Po.VendorCall;

// ── JSON options (shared by SSE serialisation) ─────────────────────────────

var JsonOpts = new JsonSerializerOptions
{
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
};

// ── ASP.NET Core setup ────────────────────────────────────────────────────
// Built early (before "compose the demo stack") so builder.Configuration — which picks up
// user-secrets automatically in Development, per Kozmo.Api's own <UserSecretsId> — is available
// to BuildCheckInTransport below. WebApplication.CreateBuilder only reads args + default config
// sources; nothing below it depends on ordering relative to this line.

var builder = WebApplication.CreateBuilder(args);

// ── Compose the demo stack ─────────────────────────────────────────────────

var catalogueDir  = FindCatalogueDir();
var profile       = new Catalogue().Load(catalogueDir);
var dbPath        = ResolveDbPath();
var store         = new SqliteEntityStore($"Data Source={dbPath}", profile);
var registry      = BuildRegistry();
await LoadPersistedVendorsAsync(store, registry);
await store.ExpireDuplicatePendingCheckInsAsync(); // one-time dedup migration (no-op when clean)
var liveCachePath         = FindLlmCachePath();         // resolved once; used for both replay and live-classify
var completenessCache     = FindCompletenessCachePath(); // separate cassette for Q&A answering
var checkInRepo           = new CheckInRepository(store);
var checkInTokenOptions   = BuildCheckInTokenOptions(builder.Configuration);
var checkInPhrasingLlm    = liveCachePath is not null ? new CachingLlmClient(liveCachePath, recordMode: false) : null;
var checkInTransport      = BuildCheckInTransport(builder.Configuration, store, checkInTokenOptions, checkInPhrasingLlm);
var slackBotToken         = Environment.GetEnvironmentVariable("KOZMO_SLACK_BOT_TOKEN") ?? builder.Configuration["Slack:BotToken"];
var slackSigningSecret    = Environment.GetEnvironmentVariable("KOZMO_SLACK_SIGNING_SECRET") ?? builder.Configuration["Slack:SigningSecret"];
var slackAckWarnMs        = int.TryParse(builder.Configuration["Slack:AckWarnThresholdMs"], out var t) ? t : 2000;
var slackHomePublisher    = new Wc.CheckIn.SlackHomeTabPublisher(slackBotToken);
var slackResponsePoster   = new Wc.CheckIn.SlackResponsePoster();
var checkInRouter         = new CheckInChannelRouter(store, checkInTransport, slackBotToken,
                               vendorId => registry.GetEntity(vendorId)?.CanonicalName);
var (facade, kyvCompleteness) = BuildKyvFacade(store, profile, registry, liveCachePath, checkInRepo, completenessCache, checkInRouter);
var sseHub                = new SseHub();
var kyvTracker            = new KyvVendorTracker();

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
builder.Services.AddSingleton(new CompletenessHolder(kyvCompleteness));
builder.Services.AddSingleton(profile);
builder.Services.AddSingleton(registry);
builder.Services.AddSingleton(sseHub);
builder.Services.AddSingleton(store);
builder.Services.AddSingleton<ICheckInRowStore>(store);
builder.Services.AddSingleton<ICheckInStore>(checkInRepo);
builder.Services.AddSingleton<ICheckInTransport>(checkInRouter);
builder.Services.AddSingleton<IOwnerChannelPrefStore>(store);
builder.Services.AddSingleton(slackHomePublisher);
builder.Services.AddSingleton(slackResponsePoster);
builder.Services.AddSingleton(checkInTokenOptions);
builder.Services.AddSingleton(kyvTracker);

// ── Vendor call run store (Phase 9d review page) ───────────────────────────
var vendorCallRunStore    = new SqliteVendorCallRunStore($"Data Source={dbPath}");
var reviewStore           = new SqlitePostMeetingReviewStore($"Data Source={dbPath}");
var reviewCheckpointStore = new SqliteReviewCheckpointStore($"Data Source={dbPath}");
var vendorUpdateNoteStore = new SqliteVendorUpdateNoteStore($"Data Source={dbPath}");
builder.Services.AddSingleton(vendorCallRunStore);
builder.Services.AddSingleton(reviewStore);
builder.Services.AddSingleton(reviewCheckpointStore);
builder.Services.AddSingleton(vendorUpdateNoteStore);

// Live LLM factory: record-mode CachingLlmClient wrapping real OpenAiLlmClient.
// Returns null when OPENAI_API_KEY is absent or fixture cache path unavailable → endpoint returns 503.
builder.Services.AddSingleton<Func<IKozmoLlm?>>(sp => () =>
{
    if (liveCachePath == null) return null;
    try { return new CachingLlmClient(liveCachePath, recordMode: true, inner: new OpenAiLlmClient()); }
    catch (InvalidOperationException) { return null; } // OPENAI_API_KEY not set
});

// ── Google Drive connector ─────────────────────────────────────────────────
var googleClientId     = Environment.GetEnvironmentVariable("GOOGLE_CLIENT_ID")     ?? builder.Configuration["GOOGLE_CLIENT_ID"]     ?? "";
var googleClientSecret = Environment.GetEnvironmentVariable("GOOGLE_CLIENT_SECRET") ?? builder.Configuration["GOOGLE_CLIENT_SECRET"] ?? "";
const string GoogleRedirectUri = "http://localhost:5000/auth/google/callback";

builder.Services.AddSingleton(new GoogleOAuthService(googleClientId, googleClientSecret, GoogleRedirectUri));
builder.Services.AddSingleton(new GoogleDriveDownloader(googleClientId, googleClientSecret));

// ── Microsoft Graph connector ──────────────────────────────────────────────
var msGraphTenantId     = Environment.GetEnvironmentVariable("MSGRAPH_TENANT_ID")     ?? builder.Configuration["MicrosoftGraph:TenantId"]     ?? "";
var msGraphClientId     = Environment.GetEnvironmentVariable("MSGRAPH_CLIENT_ID")     ?? builder.Configuration["MicrosoftGraph:ClientId"]     ?? "";
var msGraphClientSecret = Environment.GetEnvironmentVariable("MSGRAPH_CLIENT_SECRET") ?? builder.Configuration["MicrosoftGraph:ClientSecret"]  ?? "";
const string MicrosoftCallbackUri = "http://localhost:5000/auth/microsoft/callback";

MicrosoftGraphTokenProvider? msGraphProvider = null;
if (!string.IsNullOrWhiteSpace(msGraphTenantId) && !string.IsNullOrWhiteSpace(msGraphClientId) && !string.IsNullOrWhiteSpace(msGraphClientSecret))
{
    msGraphProvider = new MicrosoftGraphTokenProvider(new MicrosoftGraphOptions
    {
        TenantId     = msGraphTenantId,
        ClientId     = msGraphClientId,
        ClientSecret = msGraphClientSecret,
        RedirectUri  = MicrosoftCallbackUri,
        Scopes       = ["Calendars.Read", "Mail.Read", "User.Read", "offline_access"],
    });
}

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

// GET /vendors/{id}/trail — full glass-box reasoning chain.
// A vendor with no scored evidence yet (e.g. a KYV vendor with only structural facts) returns
// 200 with Assessed=false, not 404 — "not assessed" is a real, honest state for a known identity,
// not a missing resource. Only an unrecognized vendor id 404s.
app.MapGet("/vendors/{id}/trail", async (string id, IIiFacade f, EntityRegistry reg, SaasProfile prof) =>
{
    if (!Guid.TryParse(id, out var guid)) return Results.BadRequest("Invalid GUID");
    var entity = reg.GetEntity(guid);
    if (entity is null) return Results.NotFound();
    var trail = await f.GetReasoningTrailAsync(guid);
    if (trail is null) return Results.NotFound();
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

    // No dimension has any scored evidence yet — render identity + real belief evidence without
    // a fabricated Band/Stance, rather than a verdict manufactured from zero evidence.
    var markdown = judgement is null
        ? VendorFileRenderer.RenderNotAssessed(
            vendorId:      guid,
            vendorName:    entity.CanonicalName,
            asOf:          DemoClock.AsOf,
            activeBeliefs: activeBeliefs,
            evidence:      evidence)
        : VendorFileRenderer.Render(
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
    var writeSvc  = new VendorFileWriteService(storeInst, profile);
    var result    = await answerSvc.ProcessAnswerAsync(
        guid, req.ResponseValue, DateTimeOffset.UtcNow,
        checkInStore, writeSvc, profile, f,
        new IdentityRegistry(storeInst));

    if (result.Outcome != AnswerOutcome.Ok)
        return result.Outcome switch
        {
            AnswerOutcome.NotFound        => Results.NotFound(new { error = "Check-in not found." }),
            AnswerOutcome.AlreadyAnswered => Results.Conflict(new { error = "Check-in is already answered." }),
            AnswerOutcome.ShapeMismatch   => Results.BadRequest(new { error = "Response value does not match the expected shape." }),
            _                             => Results.Problem("Unexpected outcome.")
        };

    return Results.Ok(new { outcome = "Ok", checkInId = result.Updated!.CheckInId });
});

// POST /slack/interactivity — receives Slack block_actions button clicks (interactive components).
//
// Security model (four steps, in order):
//   1. Verify X-Slack-Signature using the signing secret and raw body — before any parsing.
//      Uses HMACSHA256 + constant-time compare. Rejects if timestamp is older than 5 minutes.
//      On any verification failure: 401, no further processing, ProcessAnswerAsync never called.
//   2. Decode the block_actions payload; extract checkInId + answer from the button value JSON.
//   3. Call ProcessAnswerAsync with "slack-user:{userId}" provenance (same path as email).
//   4. Return 200 with a JSON body that replaces the original Slack message with the recorded
//      answer — within the 3-second Slack response window; no separate outbound HTTP call needed.
//
// Anyone in the channel may answer (restriction is additive; see Part 5 of the spec).
// Idempotency is handled by ProcessAnswerAsync (second click on a PROCESSED check-in is a no-op).
app.MapPost("/slack/interactivity", async (
    HttpContext          ctx,
    ICheckInStore        checkInStore,
    IIiFacade            f,
    SaasProfile          profile,
    SqliteEntityStore    storeInst,
    SlackResponsePoster  responsePoster) =>
{
    // STEP 1 — signature verification (must happen first, using the raw body)
    if (string.IsNullOrWhiteSpace(slackSigningSecret))
    {
        Console.WriteLine("[slack] KOZMO_SLACK_SIGNING_SECRET not configured — rejecting.");
        return Results.StatusCode(401);
    }

    using var ms = new System.IO.MemoryStream();
    await ctx.Request.Body.CopyToAsync(ms);
    var rawBodyBytes = ms.ToArray();
    var rawBody      = System.Text.Encoding.UTF8.GetString(rawBodyBytes);

    var tsHeader  = ctx.Request.Headers["X-Slack-Request-Timestamp"].FirstOrDefault();
    var sigHeader = ctx.Request.Headers["X-Slack-Signature"].FirstOrDefault();

    if (!Wc.CheckIn.SlackSignatureVerifier.Verify(rawBody, tsHeader, sigHeader, slackSigningSecret))
        return Results.StatusCode(401);

    // STEP 2 — decode block_actions payload
    var form = ParseUrlEncodedForm(rawBody);
    if (!form.TryGetValue("payload", out var payloadJson) || string.IsNullOrWhiteSpace(payloadJson))
        return Results.Ok();  // ack unknown payloads silently

    Guid   checkInId;
    string answer;
    string userId;
    string responseUrl;
    try
    {
        using var doc  = System.Text.Json.JsonDocument.Parse(payloadJson);
        var root = doc.RootElement;

        if (!root.TryGetProperty("type", out var typeProp) || typeProp.GetString() != "block_actions")
            return Results.Ok();

        userId = root.TryGetProperty("user", out var userProp)
              && userProp.TryGetProperty("id",  out var idProp)
            ? idProp.GetString() ?? "unknown"
            : "unknown";

        responseUrl = root.TryGetProperty("response_url", out var ruProp)
            ? ruProp.GetString() ?? ""
            : "";

        if (!root.TryGetProperty("actions", out var actions) || actions.GetArrayLength() == 0)
            return Results.Ok();

        var actionValue = actions[0].TryGetProperty("value", out var vp) ? vp.GetString() : null;
        if (string.IsNullOrWhiteSpace(actionValue))
            return Results.Ok();

        using var av   = System.Text.Json.JsonDocument.Parse(actionValue);
        var avRoot = av.RootElement;
        if (!avRoot.TryGetProperty("checkInId", out var cidProp)
         || !avRoot.TryGetProperty("answer",    out var ansProp)
         || !Guid.TryParse(cidProp.GetString(), out checkInId))
            return Results.Ok();

        answer = ansProp.GetString() ?? "";
        if (string.IsNullOrWhiteSpace(answer))
            return Results.Ok();
    }
    catch
    {
        return Results.Ok(); // malformed payload — ack silently
    }

    // STEP 3 — fire processing in background, ack Slack immediately.
    // ProcessAnswerAsync runs the full scoring pipeline which can exceed Slack's 3-second
    // ack window. We return 200 right away and post the result back via response_url once done.
    var capturedCheckInId  = checkInId;
    var capturedAnswer     = answer;
    var capturedUserId     = userId;
    var capturedResponseUrl = responseUrl;
    _ = Task.Run(async () =>
    {
        var answerSvc = new AnswerCheckInService();
        var writeSvc  = new VendorFileWriteService(storeInst, profile);
        AnswerResult result;
        try
        {
            result = await answerSvc.ProcessAnswerAsync(
                capturedCheckInId,
                capturedAnswer.Equals("UNKNOWN", StringComparison.OrdinalIgnoreCase) ? null : capturedAnswer,
                DateTimeOffset.UtcNow,
                checkInStore, writeSvc, profile, f,
                new IdentityRegistry(storeInst),
                answeredBy: $"slack-user:{capturedUserId}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[slack] ProcessAnswerAsync exception for {capturedCheckInId}: {ex.Message}");
            return;
        }

        if (string.IsNullOrWhiteSpace(capturedResponseUrl)) return;

        var displayAnswer = capturedAnswer switch
        {
            "true"    => "Yes",
            "false"   => "No",
            "UNKNOWN" => "Unsure",
            _         => capturedAnswer
        };

        object followUp = result.Outcome == AnswerOutcome.AlreadyAnswered
            ? new { replace_original = true, text = "This check-in is already answered." }
            : (object)new
            {
                replace_original = true,
                blocks = new[]
                {
                    new
                    {
                        type = "section",
                        text = new { type = "mrkdwn", text = $"\u2713 Recorded: *{displayAnswer}* \u2014 by <@{capturedUserId}>" }
                    }
                }
            };

        await responsePoster.PostAsync(capturedResponseUrl, followUp);
    });

    // Immediate 200 ack — keeps Slack happy within the 3-second window
    return Results.Ok();
});

// POST /slack/command — /kozmo slash command (read-only; never writes beliefs or resolves check-ins).
//
// Subcommands (fixed strings only — no NLP):
//   /kozmo pending           → open check-ins for the calling user (owner-pref lookup)
//   /kozmo vendor <name>     → posture card for the named vendor (substring match)
//   /kozmo help | <anything> → usage message
//
// Security: same HMACSHA256 signing-secret verification as /slack/interactivity.
// Response: application/json ephemeral message (only the caller sees it).
app.MapPost("/slack/command", async (
    HttpContext            ctx,
    ICheckInStore          checkInStore,
    IIiFacade              f,
    EntityRegistry         reg,
    SaasProfile            profile,
    IOwnerChannelPrefStore prefStore) =>
{
    if (string.IsNullOrWhiteSpace(slackSigningSecret))
        return Results.StatusCode(401);

    using var ms = new System.IO.MemoryStream();
    await ctx.Request.Body.CopyToAsync(ms);
    var rawBody = System.Text.Encoding.UTF8.GetString(ms.ToArray());

    var tsHeader  = ctx.Request.Headers["X-Slack-Request-Timestamp"].FirstOrDefault();
    var sigHeader = ctx.Request.Headers["X-Slack-Signature"].FirstOrDefault();

    if (!Wc.CheckIn.SlackSignatureVerifier.Verify(rawBody, tsHeader, sigHeader, slackSigningSecret))
        return Results.StatusCode(401);

    var form   = ParseUrlEncodedForm(rawBody);
    var userId = form.TryGetValue("user_id", out var uid) ? uid : "unknown";
    var text   = (form.TryGetValue("text",   out var tx)  ? tx  : "").Trim();

    var spaceIdx = text.IndexOf(' ');
    var sub = (spaceIdx < 0 ? text : text[..spaceIdx]).ToLowerInvariant();
    var arg = spaceIdx < 0 ? "" : text[(spaceIdx + 1)..].Trim();

    return sub switch
    {
        "pending" => await SlackCommandPendingAsync(userId, checkInStore, prefStore),
        "vendor"  => await SlackCommandVendorAsync(arg, f, reg, profile),
        _         => Results.Ok(SlackUsagePayload())
    };
});

// POST /slack/events — Home tab event callbacks (read-only; never writes beliefs).
//
// Handles two event types:
//   url_verification  — echo the challenge string back (no signature check; Slack sends
//                       this when you first set the Request URL to validate it).
//   app_home_opened   — publish a Home tab view listing the user's open check-ins via
//                       views.publish (outbound HTTP handled by SlackHomeTabPublisher in Wc.CheckIn).
//
// All non-challenge payloads are signature-verified before processing.
app.MapPost("/slack/events", async (
    HttpContext                          ctx,
    ICheckInStore                        checkInStore,
    Wc.CheckIn.SlackHomeTabPublisher     homePublisher,
    IOwnerChannelPrefStore               prefStore) =>
{
    using var ms = new System.IO.MemoryStream();
    await ctx.Request.Body.CopyToAsync(ms);
    var rawBody = System.Text.Encoding.UTF8.GetString(ms.ToArray());

    // Handle url_verification BEFORE signature check — this is Slack's one-time URL validation
    // handshake. Per Slack's spec it must succeed before the signing secret is usable.
    try
    {
        using var doc = System.Text.Json.JsonDocument.Parse(rawBody);
        if (doc.RootElement.TryGetProperty("type", out var tp) && tp.GetString() == "url_verification"
         && doc.RootElement.TryGetProperty("challenge", out var cp))
            return Results.Ok(new { challenge = cp.GetString() });
    }
    catch { /* not JSON — fall through */ }

    // All other payloads must be signature-verified
    if (string.IsNullOrWhiteSpace(slackSigningSecret))
        return Results.StatusCode(401);

    var tsHeader  = ctx.Request.Headers["X-Slack-Request-Timestamp"].FirstOrDefault();
    var sigHeader = ctx.Request.Headers["X-Slack-Signature"].FirstOrDefault();

    if (!Wc.CheckIn.SlackSignatureVerifier.Verify(rawBody, tsHeader, sigHeader, slackSigningSecret))
        return Results.StatusCode(401);

    // Dispatch event
    try
    {
        using var doc = System.Text.Json.JsonDocument.Parse(rawBody);
        var root = doc.RootElement;
        if (!root.TryGetProperty("event", out var evtProp)) return Results.Ok();
        if (!evtProp.TryGetProperty("type", out var evtTypeProp)) return Results.Ok();

        if (evtTypeProp.GetString() == "app_home_opened"
         && evtProp.TryGetProperty("user", out var evtUserProp))
        {
            var slackUserId  = evtUserProp.GetString() ?? "unknown";
            var openCheckIns = await SlackResolveCheckInsForUserAsync(slackUserId, checkInStore, prefStore);
            await homePublisher.PublishAsync(slackUserId, openCheckIns);
        }
    }
    catch { /* malformed event — ack silently */ }

    return Results.Ok();
});

// ── Google Drive OAuth2 endpoints ─────────────────────────────────────────

// GET /auth/google — redirect browser to Google consent page
app.MapGet("/auth/google", (GoogleOAuthService oauth) =>
{
    if (!oauth.IsConfigured)
        return Results.Problem(
            detail:     "GOOGLE_CLIENT_ID and GOOGLE_CLIENT_SECRET are not configured.",
            statusCode: 503);
    return Results.Redirect(oauth.BuildAuthorizationUrl());
});

// GET /auth/google/callback — Google redirects here after user approves
app.MapGet("/auth/google/callback", async (
    string?           code,
    string?           error,
    GoogleOAuthService oauth,
    SqliteEntityStore  storeInst) =>
{
    if (!string.IsNullOrEmpty(error))
        return Results.BadRequest(new { error });
    if (string.IsNullOrEmpty(code))
        return Results.BadRequest(new { error = "No authorization code returned by Google." });

    try
    {
        var token = await oauth.ExchangeCodeAsync(code);
        await storeInst.SaveOAuthTokenAsync(
            "google", token.AccessToken, token.RefreshToken, token.ExpiresAt, token.UserEmail);

        // Redirect to the connect page after successful authorization
        return Results.Redirect("/connect");
    }
    catch (Exception ex)
    {
        return Results.Problem(detail: ex.Message, statusCode: 502);
    }
});

// GET /auth/google/status — check whether a Google account is connected
app.MapGet("/auth/google/status", async (SqliteEntityStore storeInst) =>
{
    var t = await storeInst.GetOAuthTokenAsync("google");
    if (t is null)
        return Results.Ok(new { connected = false, email = (string?)null });

    var expired = t.Value.ExpiresAt < DateTimeOffset.UtcNow;
    return Results.Ok(new { connected = true, email = t.Value.UserEmail, tokenExpired = expired });
});

// ── Microsoft Graph OAuth2 endpoints ──────────────────────────────────────

// GET /auth/microsoft — redirect browser to Microsoft Entra consent page
app.MapGet("/auth/microsoft", async (CancellationToken ct) =>
{
    if (msGraphProvider is null)
        return Results.Problem(
            detail:     "MSGRAPH_TENANT_ID, MSGRAPH_CLIENT_ID, and MSGRAPH_CLIENT_SECRET are not configured.",
            statusCode: 503);
    var url = await msGraphProvider.BuildWebAuthorizationUrlAsync(ct);
    return Results.Redirect(url);
});

// GET /auth/microsoft/callback — Entra redirects here after user approves
app.MapGet("/auth/microsoft/callback", async (
    string?           code,
    string?           error,
    SqliteEntityStore storeInst,
    CancellationToken ct) =>
{
    if (!string.IsNullOrEmpty(error))
        return Results.BadRequest(new { error });
    if (string.IsNullOrEmpty(code))
        return Results.BadRequest(new { error = "No authorization code returned by Microsoft." });
    if (msGraphProvider is null)
        return Results.Problem(detail: "Microsoft Graph is not configured.", statusCode: 503);

    try
    {
        var token = await msGraphProvider.AcquireByCodeAsync(code, ct);
        await storeInst.SaveOAuthTokenAsync(
            "microsoft", token.AccessToken, "", token.ExpiresOn, token.UserUpn, ct);
        return Results.Redirect("/connect");
    }
    catch (Exception ex)
    {
        return Results.Problem(detail: ex.Message, statusCode: 502);
    }
});

// GET /auth/microsoft/status — check whether a Microsoft account is connected
app.MapGet("/auth/microsoft/status", async (SqliteEntityStore storeInst, CancellationToken ct) =>
{
    var t = await storeInst.GetOAuthTokenAsync("microsoft", ct);
    if (t is null)
        return Results.Ok(new { connected = false, email = (string?)null });

    var expired = t.Value.ExpiresAt < DateTimeOffset.UtcNow;
    return Results.Ok(new { connected = true, email = t.Value.UserEmail, tokenExpired = expired });
});

// POST /kyv/run — download files from Google Drive then run the KYV pipeline
app.MapPost("/kyv/run", async (
    KyvRunRequest          request,
    SqliteEntityStore      storeInst,
    GoogleOAuthService     oauth,
    GoogleDriveDownloader  downloader,
    ICheckInStore          checkInStore,
    SseHub                 hub,
    EntityRegistry         entityRegistry,
    KyvVendorTracker       kyvTracker,
    Func<IKozmoLlm?>       liveLlmFactory,
    SaasProfile            profile,
    CompletenessHolder     completenessHolder) =>
{
    if (string.IsNullOrWhiteSpace(request.DriveUrl))
        return Results.BadRequest(new { error = "driveUrl must not be empty." });

    // Load stored token; refresh if expired
    var stored = await storeInst.GetOAuthTokenAsync("google");
    if (stored is null)
        return Results.Problem(
            detail:     "Not connected to Google Drive. Visit /auth/google first.",
            statusCode: 401);

    var token = new OAuthToken(stored.Value.AccessToken, stored.Value.RefreshToken,
                               stored.Value.ExpiresAt,   stored.Value.UserEmail);

    if (token.ExpiresAt < DateTimeOffset.UtcNow.AddMinutes(2))
    {
        try
        {
            token = await oauth.RefreshAsync(token.RefreshToken);
            await storeInst.SaveOAuthTokenAsync(
                "google", token.AccessToken, token.RefreshToken, token.ExpiresAt, token.UserEmail);
        }
        catch (Exception ex)
        {
            return Results.Problem(
                detail:     $"Token refresh failed — re-authorize at /auth/google. ({ex.Message})",
                statusCode: 401);
        }
    }

    // Live LLM required — KYV candidate extraction uses real GPT-4o-mini
    var liveLlm = liveLlmFactory();
    if (liveLlm is null)
        return Results.Problem(
            detail:     "OPENAI_API_KEY is not set or fixture cache path is unavailable.",
            statusCode: 503);

    // Broadcast: started
    hub.Broadcast(JsonSerializer.Serialize(
        new { type = "kyv-started", ts = DateTimeOffset.UtcNow,
              data = new { driveUrl = request.DriveUrl } }, JsonOpts));

    // Download Drive files to a local temp folder
    string tempFolder;
    try
    {
        hub.Broadcast(JsonSerializer.Serialize(
            new { type = "kyv-downloading", ts = DateTimeOffset.UtcNow,
                  data = new { driveUrl = request.DriveUrl } }, JsonOpts));

        tempFolder = await downloader.DownloadToTempFolderAsync(token, request.DriveUrl);

        var fileCount = Directory.GetFiles(tempFolder, "*.pdf", SearchOption.AllDirectories).Length;
        hub.Broadcast(JsonSerializer.Serialize(
            new { type = "kyv-downloading", ts = DateTimeOffset.UtcNow,
                  data = new { fileCount } }, JsonOpts));
    }
    catch (Exception ex)
    {
        hub.Broadcast(JsonSerializer.Serialize(
            new { type = "kyv-error", ts = DateTimeOffset.UtcNow,
                  data = new { error = ex.Message } }, JsonOpts));
        return Results.Problem(detail: $"Drive download failed: {ex.Message}", statusCode: 502);
    }

    try
    {
        // Run the KYV pipeline — inputs: temp folder of .pdf files
        // entity-type classifier defaults to Company for ambiguous names (same as offline tests)
        var runner = new KyvProgramRunner(
            llm:              liveLlm,
            entityClassifier: new AlwaysCompanyClassifier(),
            registry:         new IdentityRegistry(storeInst),
            checkInStore:     checkInStore,
            entityStore:      storeInst,
            profile:          profile,
            completeness:     completenessHolder.Value,
            spineRegistry:    entityRegistry,
            transport:        checkInTransport);

        var run = await runner.RunAsync(tempFolder, DateTimeOffset.UtcNow);

        // Replay stages over SSE for live UI feedback
        foreach (var stage in run.Stages)
        {
            await Task.Delay(250);
            hub.Broadcast(JsonSerializer.Serialize(
                new { type = "kyv-stage", ts = DateTimeOffset.UtcNow,
                      data = new { name = stage.StageName, order = stage.StageOrder,
                                   itemsProcessed = stage.ItemsProcessed } }, JsonOpts));
        }

        hub.Broadcast(JsonSerializer.Serialize(
            new { type = "kyv-complete", ts = DateTimeOffset.UtcNow,
                  data = new { runId = run.RunId, status = run.Status.ToString() } }, JsonOpts));

        // Sync EntityRegistry so /vendors immediately reflects KYV-discovered vendors.
        // LoadVendorsAsync() (program_run_id IS NULL) never sees these — KYV's Stage F always
        // stamps a non-null program_run_id for run isolation. Use the run-scoped query instead.
        var allVendors = await storeInst.LoadVendorsByRunAsync(run.RunId);
        foreach (var (vid, vname, vren) in allVendors)
            entityRegistry.Register(vid, vname, vren);

        // Track which vendor IDs were discovered by KYV (exclude pre-seeded demo vendors)
        var seededSet = new HashSet<Guid>(SeedData.VendorIds);
        kyvTracker.RecordDiscovered(allVendors.Select(v => v.Item1).Where(id => !seededSet.Contains(id)));

        return Results.Ok(new
        {
            runId              = run.RunId,
            programName        = run.ProgramName,
            sourceFolder       = run.SourceFolder,
            status             = run.Status.ToString(),
            startedAt          = run.StartedAt,
            finishedAt         = run.FinishedAt,
            stages             = run.Stages.Select(s => new
            {
                order          = s.StageOrder,
                name           = s.StageName,
                itemsProcessed = s.ItemsProcessed
            }),
            unreadableDocuments = run.UnreadableDocuments.Select(u => new
            {
                path   = u.RelativePath,
                reason = u.Reason
            })
        });
    }
    finally
    {
        // Best-effort cleanup of temp folder
        try { Directory.Delete(tempFolder, recursive: true); } catch { /* ignore */ }
    }
});

// GET /kyv/vendors — vendors discovered through KYV ingestion (excludes seeded demo data)
app.MapGet("/kyv/vendors", async (IIiFacade f, EntityRegistry reg, SaasProfile prof, KyvVendorTracker tracker) =>
{
    if (!tracker.HasRun) return Results.Ok(Array.Empty<VendorSummaryDto>());
    var now     = DemoClock.AsOf;
    var vendors = new List<VendorSummaryDto>();
    foreach (var id in tracker.DiscoveredIds    )
    {
        var entity = reg.GetEntity(id);
        if (entity is null) continue;
        var idx = await f.GetIndexAsync(id);
        var pos = await f.GetPostureAsync(id);
        vendors.Add(DtoMapper.ToSummary(id, entity, idx, pos, now));
    }
    return Results.Ok(vendors);
});

app.Run();

// ── Local helpers ─────────────────────────────────────────────────────────

static string ResolveDbPath()
{
    var envPath = Environment.GetEnvironmentVariable("KOZMO_DB_PATH");
    if (!string.IsNullOrWhiteSpace(envPath)) return envPath;

    var dir = AppContext.BaseDirectory;
    while (!string.IsNullOrEmpty(dir))
    {
        var candidate = Path.Combine(dir, "kozmo-demo.db");
        if (File.Exists(candidate)) return candidate;
        var parent = Path.GetDirectoryName(dir);
        if (parent == dir) break;
        dir = parent;
    }

    // Fall back to output directory (fresh DB will be created there)
    return Path.Combine(AppContext.BaseDirectory, "kozmo-demo.db");
}

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

    var kyvVendors = await store.LoadAllKyvVendorsAsync();
    foreach (var (id, name, renewalDate) in kyvVendors)
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
    reg.Register(Guid.Parse("eeeeeeee-0005-0000-0000-000000000001"), "Northwind Systems");
    reg.Register(Guid.Parse("eeeeeeee-0006-0000-0000-000000000001"), "Ridgeline Software");
    reg.Register(Guid.Parse("dd000001-0000-0000-0000-000000000001"), "Northstar Software",
        new DateTimeOffset(2026, 9, 28, 0, 0, 0, TimeSpan.Zero));
    return reg;
}

// KYV facade: same as BuildFacade but with a real CompletenessOrchestrator wired in.
// The completeness orchestrator fires inside RecomputeVendorAsync (the §5 synchronous hook),
// which is called by ProcessCheckInService when a DIMENSION_GAP check-in is answered.
// Legacy path (BuildFacade) stays null — signal submission and demo recomputes are LLM-free.
// Also returns the CompletenessOrchestrator itself so callers other than the facade (e.g.
// POST /kyv/run's KyvProgramRunner, stage 8) can reuse the SAME instance rather than building
// a second one against the same cassette.
static (IiFacade Facade, CompletenessOrchestrator? Completeness) BuildKyvFacade(
    IEntityStore  store, SaasProfile profile, EntityRegistry registry,
    string?       llmCachePath, ICheckInStore checkInStore, string? completenessCache,
    ICheckInTransport? transport = null)
{
    IKozmoLlm? llm = null;
    if (llmCachePath != null)
    {
        var info = new FileInfo(llmCachePath);
        if (info.Length > 4)
            llm = new CachingLlmClient(llmCachePath, recordMode: false);
    }

    CompletenessOrchestrator? completeness = null;
    if (completenessCache != null)
    {
        var info = new FileInfo(completenessCache);
        if (info.Length > 4)
        {
            var answeringLlm = new CachingLlmClient(completenessCache, recordMode: false);
            completeness = new CompletenessOrchestrator(
                new QuestionAnsweringStage(answeringLlm, profile),
                new GapCheckInStage(),
                checkInStore,
                DepthLevel.L1,
                "kyv@kozmo",
                transport);
        }
    }

    var facade = new IiFacade(
        new ObservationModule(llm),
        new RubricModule(),
        new IndexModule(),
        new PostureModule(),
        new DecayEngine(),
        store,
        profile,
        registry,
        DemoClock.Fixed,
        completeness);

    return (facade, completeness);
}

// Builds CheckInTokenOptions from config/env. The secret is required when Brevo SMTP is active.
// TtlDays defaults to 7 if not set. UiBaseUrl defaults to http://localhost:3000.
// ApiBaseUrl defaults to http://localhost:5000 (the ASP.NET Core host serving Razor Pages).
static CheckInTokenOptions BuildCheckInTokenOptions(IConfiguration config)
{
    var secret     = config["CheckIn:TokenSecret"]
                     ?? Environment.GetEnvironmentVariable("KOZMO_CHECKIN_TOKEN_SECRET")
                     ?? "dev-secret-change-in-production";
    var ttlDays    = int.TryParse(
                         config["CheckIn:LinkTtlDays"]
                         ?? Environment.GetEnvironmentVariable("KOZMO_CHECKIN_LINK_TTL_DAYS"),
                         out var d) ? d : 7;
    var uiBaseUrl  = Environment.GetEnvironmentVariable("KOZMO_UI_BASE_URL")  ?? "http://localhost:3000";
    var apiBaseUrl = Environment.GetEnvironmentVariable("KOZMO_API_BASE_URL") ?? "http://localhost:5000";
    return new CheckInTokenOptions(secret, ttlDays, uiBaseUrl, apiBaseUrl);
}

// Real email when Brevo:SmtpKey/Brevo:SenderEmail (config — user-secrets in Development,
// courtesy of Kozmo.Api's own <UserSecretsId> — or BREVO_SMTP_KEY/BREVO_SENDER_EMAIL env vars as
// a fallback) are configured; otherwise the existing in-app no-op — same seam
// InAppCheckInTransport's own doc comment predicted ("a real-email implementation swaps in here
// with no changes to the loop or processing code"). The SMTP key and sender are the only secrets
// here — config/env only, NEVER hardcoded, NEVER written to a file this repo tracks (user-secrets
// lives outside the repo entirely, under %APPDATA%\Microsoft\UserSecrets\<UserSecretsId>\). Host/
// port/login are not secret (Brevo account identifiers) but stay overridable for flexibility.
// Logs a warning if Brevo is active but KOZMO_CHECKIN_TOKEN_SECRET is still the dev placeholder.
// The page model resolves CheckInTokenOptions from DI (where the fixture can override it),
// so the guard is advisory rather than a hard throw.
// ── Slack Phase 3 read-only helpers ──────────────────────────────────────
// None of these call ProcessAnswerAsync or any belief-write path.

static async Task<IResult> SlackCommandPendingAsync(
    string                 userId,
    ICheckInStore          checkInStore,
    IOwnerChannelPrefStore prefStore)
{
    var checkIns = await SlackResolveCheckInsForUserAsync(userId, checkInStore, prefStore);

    if (checkIns.Count == 0)
        return Results.Ok(new { response_type = "ephemeral",
            text = "No open check-ins \u2014 all clear." });

    var blocks = new List<object>
    {
        new { type = "header", text = new { type = "plain_text",
            text = $"Open Check-ins ({checkIns.Count})", emoji = false } },
        new { type = "divider" }
    };
    foreach (var ci in checkIns)
    {
        blocks.Add(new { type = "section",
            text = new { type = "mrkdwn",
                text = $"*{ci.Question}*\n_Raised:_ {ci.RaisedAt:MMM d, yyyy}" } });
    }
    return Results.Ok(new { response_type = "ephemeral", blocks });
}

static async Task<IResult> SlackCommandVendorAsync(
    string         vendorName,
    IIiFacade      f,
    EntityRegistry reg,
    SaasProfile    profile)
{
    if (string.IsNullOrWhiteSpace(vendorName))
        return Results.Ok(SlackUsagePayload());

    var matches = reg.GetAllIds()
        .Select(id => (id, entity: reg.GetEntity(id)))
        .Where(m => m.entity is not null &&
                    m.entity.CanonicalName.Contains(vendorName, StringComparison.OrdinalIgnoreCase))
        .ToList();

    if (matches.Count == 0)
        return Results.Ok(new { response_type = "ephemeral",
            text = $"No vendor found matching \"{vendorName}\"." });

    if (matches.Count > 1)
    {
        var names = string.Join(", ", matches.Select(m => $"*{m.entity!.CanonicalName}*"));
        return Results.Ok(new { response_type = "ephemeral",
            text = $"Multiple vendors match \"{vendorName}\": {names}. Please be more specific." });
    }

    var (vendorId, vendorEntity) = matches[0];
    var idx     = await f.GetIndexAsync(vendorId);
    var posture = await f.GetPostureAsync(vendorId);

    var fields = new object[]
    {
        new { type = "mrkdwn", text = $"*Band:* {idx?.Band.ToString() ?? "Unknown"}" },
        new { type = "mrkdwn", text = $"*Stance:* {posture?.Stance.ToString() ?? "Unknown"}" },
        new { type = "mrkdwn",
              text = $"*Confidence:* {(posture is not null ? posture.Confidence.ToString("P0") : "\u2014")}" },
        new { type = "mrkdwn",
              text = $"*Composite:* {(idx is not null ? idx.Composite.ToString("P0") : "\u2014")}" }
    };
    var blocks = new object[]
    {
        new { type = "header", text = new { type = "plain_text",
            text = vendorEntity!.CanonicalName, emoji = false } },
        new { type = "section", fields }
    };
    return Results.Ok(new { response_type = "ephemeral", blocks });
}

static object SlackUsagePayload() => new
{
    response_type = "ephemeral",
    text = "*Kozmo \u2014 Usage*\n`/kozmo pending` \u2014 list your open check-ins\n" +
           "`/kozmo vendor <name>` \u2014 vendor posture card\n`/kozmo help` \u2014 this message"
};

static async Task<IReadOnlyList<Wc.Contracts.CheckIn>> SlackResolveCheckInsForUserAsync(
    string                 slackUserId,
    ICheckInStore          checkInStore,
    IOwnerChannelPrefStore prefStore)
{
    var allOpen  = await checkInStore.GetOpenAsync();
    var allPrefs = await prefStore.GetAllOwnerChannelPrefsAsync();
    // DM-pref lookup: find the owner email mapped to this Slack user ID
    var pref = allPrefs.FirstOrDefault(p =>
        p.Channel == "Slack" && p.SlackDestination == slackUserId);
    return pref is not null
        ? allOpen.Where(ci => ci.Owner == pref.OwnerId).ToList()
        : allOpen.ToList();
}

static Dictionary<string, string> ParseUrlEncodedForm(string body)
{
    var result = new Dictionary<string, string>(StringComparer.Ordinal);
    if (string.IsNullOrEmpty(body)) return result;
    foreach (var pair in body.Split('&'))
    {
        var idx = pair.IndexOf('=');
        if (idx <= 0) continue;
        var key = Uri.UnescapeDataString(pair[..idx].Replace('+', ' '));
        var val = Uri.UnescapeDataString(pair[(idx + 1)..].Replace('+', ' '));
        result[key] = val;
    }
    return result;
}

static ICheckInTransport BuildCheckInTransport(
    IConfiguration config, SqliteEntityStore store, CheckInTokenOptions tokenOptions,
    IKozmoLlm? phrasingLlm = null)
{
    var smtpHost  = Environment.GetEnvironmentVariable("BREVO_SMTP_HOST")  ?? "smtp-relay.brevo.com";
    var smtpPort  = int.TryParse(Environment.GetEnvironmentVariable("BREVO_SMTP_PORT"), out var p) ? p : 587;
    var smtpLogin = config["Brevo:SmtpUser"]
                     ?? Environment.GetEnvironmentVariable("BREVO_SMTP_LOGIN")
                     ?? "9f924d001@smtp-brevo.com"; // not secret — Brevo account's fixed SMTP login
    var smtpKey   = config["Brevo:SmtpKey"] ?? Environment.GetEnvironmentVariable("BREVO_SMTP_KEY");

    var senderEmail = config["Brevo:SenderEmail"] ?? Environment.GetEnvironmentVariable("BREVO_SENDER_EMAIL");
    var senderName  = Environment.GetEnvironmentVariable("BREVO_SENDER_NAME") ?? "Kozmo Check-ins";
    var recipientName = Environment.GetEnvironmentVariable("CHECKIN_RECIPIENT_NAME") ?? "Kozmo Demo";

    if (string.IsNullOrWhiteSpace(smtpKey) || string.IsNullOrWhiteSpace(senderEmail))
    {
        Console.WriteLine("[checkin-transport] Brevo:SmtpKey/SenderEmail not configured " +
                           "-> using InAppCheckInTransport (no real email will be sent).");
        return new InAppCheckInTransport();
    }

    // Warn if the secret is still the dev placeholder — in production this MUST be overridden.
    // We log rather than throw so WebApplicationFactory test runs (which replace the DI singleton)
    // are not blocked; the page model always resolves CheckInTokenOptions from DI, not from here.
    if (tokenOptions.Secret == "dev-secret-change-in-production")
        Console.WriteLine("[checkin-transport] WARNING: KOZMO_CHECKIN_TOKEN_SECRET is not set. " +
                           "Links will use the insecure dev placeholder — change for production. " +
                           "Set via user-secrets (CheckIn:TokenSecret) or KOZMO_CHECKIN_TOKEN_SECRET env var.");

    // Recipient resolved dynamically at send time from the logged-in Google account.
    // If no user is connected yet the send is a no-op (returns without error).
    Func<CancellationToken, Task<string?>> recipientResolver =
        async ct => (await store.GetOAuthTokenAsync("google", ct))?.UserEmail;

    Console.WriteLine($"[checkin-transport] Brevo SMTP transport selected — sender={senderEmail}, " +
                       $"recipient=<logged-in Google account>, host={smtpHost}:{smtpPort}.");
    return new BrevoCheckInTransport(
        smtpHost, smtpPort, smtpLogin, smtpKey, senderEmail, senderName,
        recipientResolver, recipientName, tokenOptions,
        llm: phrasingLlm, entityStore: store);
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

static string? FindCompletenessCachePath()
{
    var dir = AppContext.BaseDirectory;
    while (!string.IsNullOrEmpty(dir))
    {
        var candidate = Path.Combine(dir, "fixtures", "completeness", "answering.cassette.json");
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

/// <summary>
/// Tracks vendor IDs discovered via KYV ingestion runs (excludes pre-seeded demo vendors).
/// </summary>
public sealed class KyvVendorTracker
{
    private readonly HashSet<Guid> _discovered = [];

    public bool HasRun { get; private set; }
    public IReadOnlyCollection<Guid> DiscoveredIds => _discovered;

    public void RecordDiscovered(IEnumerable<Guid> ids)
    {
        HasRun = true;
        foreach (var id in ids) _discovered.Add(id);
    }
}

record CheckInAnswerRequest(string? ResponseValue);
record KyvRunRequest(string DriveUrl);

// Nullable-singleton wrapper — Microsoft.Extensions.DependencyInjection's AddSingleton(TService)
// overload throws on a null instance, so a possibly-null CompletenessOrchestrator (absent when
// the completeness cassette isn't present) is registered wrapped in this instead.
sealed record CompletenessHolder(CompletenessOrchestrator? Value);

// Fallback entity-type classifier: defaults ambiguous names to Company.
// Deterministic rules in EntityTypeClassificationStage handle most cases;
// this only fires for names that survive all rule checks as Unknown.
file sealed class AlwaysCompanyClassifier : IEntityTypeClassifier
{
    public Task<EntityType> ClassifyAsync(
        string effectiveName, string comparisonKey, CancellationToken ct = default)
        => Task.FromResult(EntityType.Company);
}
