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
using Km.Store.Metadata;
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
// E2.2a — boot-time coherence check Catalogue.Validate can't do itself (Km.Store must not
// reference Ii.Completeness); see QuestionBankValidator's doc comment.
QuestionBankValidator.ValidateBindings(profile);
var dbPath        = Path.Combine(AppContext.BaseDirectory, "kozmo-demo.db");
var store         = new SqliteEntityStore($"Data Source={dbPath}", profile);
// E1 Part 7 Step 5 wiring — same db file as SqliteEntityStore, own table (document_metadata), own
// connection. Never read by scoring/completeness (CI-enforced metadata wall); agent-facing only.
var metadataStore = new SqliteMetadataStore($"Data Source={dbPath}");
var registry      = BuildRegistry();
await LoadPersistedVendorsAsync(store, registry);
var liveCachePath         = FindLlmCachePath();         // resolved once; used for both replay and live-classify
var completenessCache     = FindCompletenessCachePath(); // separate cassette for Q&A answering
var checkInRepo           = new CheckInRepository(store);
var checkInTransport      = BuildCheckInTransport(builder.Configuration);
var (facade, kyvCompleteness) = BuildKyvFacade(store, profile, registry, liveCachePath, checkInRepo, completenessCache, checkInTransport);
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
builder.Services.AddSingleton<IMetadataStore>(metadataStore);
builder.Services.AddSingleton<ICheckInRowStore>(store);
builder.Services.AddSingleton<ICheckInStore>(checkInRepo);
builder.Services.AddSingleton<ICheckInTransport>(checkInTransport);
builder.Services.AddSingleton(kyvTracker);

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

    // Document retention (see KYV_KNOWN_GAPS.md) — this endpoint previously read the upload
    // straight into memory and discarded it once the request finished; content + content_text at
    // minimum, no drive_file_id (manual upload, not sourced from Drive). Deterministic id is a
    // content hash — the exact same file uploaded twice reuses the same document row.
    var contentText = string.Join("\n", pageTexts.OrderBy(kv => kv.Key).Select(kv => kv.Value));
    var documentId  = DocumentPersistenceStage.ComputeDocumentId(driveFileId: null, pdfBytes);
    await storeInst.UpsertDocumentAsync(new DocumentRow(
        Id:          documentId,
        ProgramId:   null,
        VendorId:    vendorId,
        Filename:    file.FileName,
        Content:     pdfBytes,
        ContentText: string.IsNullOrEmpty(contentText) ? null : contentText,
        DriveFileId: null,
        IngestedAt:  DemoClock.AsOf), default);

    // Recompute posture so the vendor-file Razor page sees fresh state on arrival — persisted the
    // same way Kyv.ProgramRunner Stage 9 persists it, so GET /vendors/{id} sees it too (previously
    // discarded; RecomputeVendorAsync itself never persists — see IIiFacade.cs doc comment).
    var judgement = await f.RecomputeVendorAsync(vendorId);
    if (judgement is not null)
    {
        await storeInst.SaveIndexAsync(judgement.Index);
        await storeInst.AppendPostureAsync(judgement.Posture);
    }

    return Results.Ok(new { vendorId = vendorId.ToString(), documentId = documentId.ToString() });
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

// GET /vendors/{id}/metadata — retained, non-scored clauses (E1 metadata) for a vendor.
// Agent-facing only: never confidence-scored, never read by scoring/completeness (CI-enforced
// metadata wall in Kozmo.Architecture.Tests).
app.MapGet("/vendors/{id}/metadata", async (
    string          id,
    EntityRegistry  reg,
    IMetadataStore  metadataStore) =>
{
    if (!Guid.TryParse(id, out var guid)) return Results.BadRequest("Invalid GUID");
    if (reg.GetEntity(guid) is null) return Results.NotFound();

    var knowledge = await metadataStore.GetForEntityAsync(guid);

    return Results.Ok(new
    {
        vendorId = guid,
        clauses  = knowledge.Metadata.Select(m => new
        {
            field        = m.FieldName,
            value        = m.Value,
            derivation   = m.Derivation,
            documentType = m.DocumentType,
            observedAt   = m.ObservedAt
        })
    });
});

// GET /vendors/{id}/questions?dimension=Operational — ALL SIX authored completeness questions
// (SaasQuestionBank — doctrine, never LLM-generated, L1+L2+L3) for one dimension, cross-referenced
// against this vendor's real current beliefs. Only the two bound questions system-wide (sla_uptime ->
// Operational, csat -> Experiential) can ever resolve a real answer; every other question is an
// honest "not yet reviewed" gap, not a fabricated one — there is no path today that writes a
// Dimension/Criterion-scoped belief for an unbound question.
app.MapGet("/vendors/{id}/questions", async (
    string            id,
    string?           dimension,
    EntityRegistry    reg,
    SqliteEntityStore storeInst) =>
{
    if (!Guid.TryParse(id, out var guid)) return Results.BadRequest("Invalid GUID");
    if (reg.GetEntity(guid) is null) return Results.NotFound();
    if (!Enum.TryParse<Dimension>(dimension, ignoreCase: true, out var dim))
        return Results.BadRequest("Invalid or missing dimension");

    var beliefs = await storeInst.GetCurrentBeliefsAsync(guid);

    var questions = SaasQuestionBank.All
        .Where(q => q.Dimension == dim)
        .OrderBy(q => q.DepthLevel)
        .ThenBy(q => q.Id, StringComparer.Ordinal)
        .Select(q =>
        {
            var belief = q.TargetClaimKey is not null
                ? beliefs.FirstOrDefault(b => b.Criterion == q.TargetClaimKey)
                : null;

            return new
            {
                id             = q.Id,
                text           = q.Text,
                depthLevel     = q.DepthLevel.ToString(),
                answerType     = q.AnswerType.ToString(),
                targetClaimKey = q.TargetClaimKey,
                answered       = belief is not null,
                belief         = belief is null ? null : new
                {
                    value      = belief.Value,
                    derivation = string.IsNullOrEmpty(belief.Derivation) ? null : belief.Derivation,
                    sourceTier = belief.SourceTier.ToString(),
                    confidence = belief.Confidence,
                    criterion  = belief.Criterion
                }
            };
        })
        .ToList();

    return Results.Ok(new { dimension = dim.ToString(), questions });
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
        DateTimeOffset.UtcNow, entityStore: storeInst);

    return Results.Ok(new { outcome = "Ok", checkInId = answer.Updated!.CheckInId });
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
    CompletenessHolder     completenessHolder,
    IMetadataStore         metadataStore) =>
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
    IReadOnlyDictionary<string, string> driveFileIdsByFilename;
    try
    {
        hub.Broadcast(JsonSerializer.Serialize(
            new { type = "kyv-downloading", ts = DateTimeOffset.UtcNow,
                  data = new { driveUrl = request.DriveUrl } }, JsonOpts));

        var downloadResult = await downloader.DownloadToTempFolderAsync(token, request.DriveUrl);
        tempFolder              = downloadResult.TempDir;
        driveFileIdsByFilename  = downloadResult.DriveFileIdsByFilename;

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
            metadataStore:    metadataStore,
            documentStore:    storeInst);

        var run = await runner.RunAsync(tempFolder, DateTimeOffset.UtcNow, driveFileIdsByFilename);

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

// POST /kyv/run-local — ingest a LOCAL folder through the identical KYV pipeline as POST /kyv/run,
// skipping the Google Drive download hop entirely. No OAuth is required here: in the Drive path,
// GoogleOAuthService/GoogleDriveDownloader exist solely to fetch files onto local disk before the
// pipeline runs — once files are local, KyvProgramRunner.RunAsync never touches Drive or OAuth
// itself. This calls the SAME RunAsync the Drive path and the test suite (KyvProgramRunnerTests,
// CompletenessWiringTests) already exercise — identical six identity stages
// (Normalize→Classify→Cluster→Annotate→Assign→Write), identical belief/metadata persistence,
// identical completeness/index hooks. Not forked.
app.MapPost("/kyv/run-local", async (
    KyvRunLocalRequest     request,
    SqliteEntityStore      storeInst,
    ICheckInStore          checkInStore,
    SseHub                 hub,
    EntityRegistry         entityRegistry,
    KyvVendorTracker       kyvTracker,
    Func<IKozmoLlm?>       liveLlmFactory,
    SaasProfile            profile,
    CompletenessHolder     completenessHolder,
    IMetadataStore         metadataStore) =>
{
    if (string.IsNullOrWhiteSpace(request.LocalPath))
        return Results.BadRequest(new { error = "localPath must not be empty." });

    if (!Directory.Exists(request.LocalPath))
        return Results.BadRequest(new { error = $"localPath does not exist: {request.LocalPath}" });

    // Live LLM required — same requirement as the Drive path; KYV candidate/belief extraction
    // uses real GPT-4o-mini in record mode (cached afterward, never re-called for the same
    // document text — see CachingLlmClient).
    var liveLlm = liveLlmFactory();
    if (liveLlm is null)
        return Results.Problem(
            detail:     "OPENAI_API_KEY is not set or fixture cache path is unavailable.",
            statusCode: 503);

    hub.Broadcast(JsonSerializer.Serialize(
        new { type = "kyv-started", ts = DateTimeOffset.UtcNow,
              data = new { localPath = request.LocalPath } }, JsonOpts));

    // Same construction as POST /kyv/run (entity-type classifier defaults to Company for
    // ambiguous names, same as offline tests) — only the input (a local folder, no temp-download
    // step, no driveFileIdsByFilename) differs.
    var runner = new KyvProgramRunner(
        llm:              liveLlm,
        entityClassifier: new AlwaysCompanyClassifier(),
        registry:         new IdentityRegistry(storeInst),
        checkInStore:     checkInStore,
        entityStore:      storeInst,
        profile:          profile,
        completeness:     completenessHolder.Value,
        spineRegistry:    entityRegistry,
        metadataStore:    metadataStore,
        documentStore:    storeInst);

    var run = await runner.RunAsync(request.LocalPath, DateTimeOffset.UtcNow);

    // Replay stages over SSE for live UI feedback — same as the Drive path.
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

    // Sync EntityRegistry so /vendors immediately reflects KYV-discovered vendors — same as the
    // Drive path (LoadVendorsAsync excludes KYV rows by design; use the run-scoped query instead).
    var allVendors = await storeInst.LoadVendorsByRunAsync(run.RunId);
    foreach (var (vid, vname, vren) in allVendors)
        entityRegistry.Register(vid, vname, vren);

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
});

// GET /kyv/vendors — vendors discovered through KYV ingestion (excludes seeded demo data).
// Sourced from the vendors table's program_run_id column (non-null only for KYV-discovered rows,
// always absent for seeds — see LoadAllKyvVendorsAsync), not in-memory run tracking, so this stays
// correct across a process restart instead of going empty until the next /kyv/run call.
app.MapGet("/kyv/vendors", async (IIiFacade f, EntityRegistry reg, SqliteEntityStore storeInst) =>
{
    var now        = DemoClock.AsOf;
    var vendors    = new List<VendorSummaryDto>();
    var discovered = await storeInst.LoadAllKyvVendorsAsync();
    foreach (var (id, _, _) in discovered)
    {
        var entity = reg.GetEntity(id);
        if (entity is null) continue;
        var idx = await f.GetIndexAsync(id);
        var pos = await f.GetPostureAsync(id);
        vendors.Add(DtoMapper.ToSummary(id, entity, idx, pos, now));
    }
    return Results.Ok(vendors);
});

// GET /documents/{id} — metadata + extracted text for a retained source document. Never returns
// raw bytes here (see /documents/{id}/raw) — a metadata fetch should never accidentally serve a
// multi-hundred-KB blob.
app.MapGet("/documents/{id}", async (string id, SqliteEntityStore storeInst) =>
{
    if (!Guid.TryParse(id, out var docId))
        return Results.BadRequest(new { error = "invalid document id" });

    var doc = await storeInst.GetDocumentAsync(docId);
    if (doc is null) return Results.NotFound();

    return Results.Ok(new
    {
        id          = doc.Id,
        vendorId    = doc.VendorId,
        filename    = doc.Filename,
        contentText = doc.ContentText,
        driveFileId = doc.DriveFileId,
        hasContent  = doc.Content is not null,
        ingestedAt  = doc.IngestedAt
    });
});

// GET /documents/{id}/raw — the actual retained PDF bytes, only when explicitly requested.
app.MapGet("/documents/{id}/raw", async (string id, SqliteEntityStore storeInst) =>
{
    if (!Guid.TryParse(id, out var docId))
        return Results.BadRequest(new { error = "invalid document id" });

    var doc = await storeInst.GetDocumentAsync(docId);
    if (doc is null || doc.Content is null) return Results.NotFound();

    return Results.File(doc.Content, "application/pdf", doc.Filename);
});

// GET /programs — real programs (just one today: "Vendor Cleanup Program"). A Program is a
// durable container that can span multiple ingestion runs; program_run_id (one run) belongs to
// program_id (its container) — see SqliteEntityStore.MigratePrograms.
app.MapGet("/programs", async (SqliteEntityStore storeInst) =>
{
    var programs = await storeInst.GetAllProgramsAsync();
    return Results.Ok(programs.Select(p => new ProgramDto(p.Id, p.Name, p.CreatedAt)));
});

// GET /programs/{id}/vendors — real vendors scoped to one program, across all of its runs.
// Mirrors GET /kyv/vendors exactly, just filtered by program_id instead of "every KYV run".
app.MapGet("/programs/{id}/vendors", async (string id, IIiFacade f, EntityRegistry reg, SqliteEntityStore storeInst) =>
{
    if (!Guid.TryParse(id, out var programId))
        return Results.BadRequest(new { error = "invalid program id" });

    var now        = DemoClock.AsOf;
    var vendors    = new List<VendorSummaryDto>();
    var discovered = await storeInst.LoadKyvVendorsByProgramAsync(programId);
    foreach (var (vendorId, _, _) in discovered)
    {
        var entity = reg.GetEntity(vendorId);
        if (entity is null) continue;
        var idx = await f.GetIndexAsync(vendorId);
        var pos = await f.GetPostureAsync(vendorId);
        vendors.Add(DtoMapper.ToSummary(vendorId, entity, idx, pos, now));
    }
    return Results.Ok(vendors);
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

    // KYV-discovered vendors are excluded from LoadVendorsAsync by design (run isolation), but
    // still need to survive a process restart — otherwise a vendor ingested by a prior process
    // instance vanishes from /vendors even though its beliefs/checkins remain in SQLite.
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

// Real email when Brevo:SmtpKey/Brevo:SenderEmail (config — user-secrets in Development,
// courtesy of Kozmo.Api's own <UserSecretsId> — or BREVO_SMTP_KEY/BREVO_SENDER_EMAIL env vars as
// a fallback) are configured; otherwise the existing in-app no-op — same seam
// InAppCheckInTransport's own doc comment predicted ("a real-email implementation swaps in here
// with no changes to the loop or processing code"). The SMTP key and sender are the only secrets
// here — config/env only, NEVER hardcoded, NEVER written to a file this repo tracks (user-secrets
// lives outside the repo entirely, under %APPDATA%\Microsoft\UserSecrets\<UserSecretsId>\). Host/
// port/login are not secret (Brevo account identifiers) but stay overridable for flexibility.
static ICheckInTransport BuildCheckInTransport(IConfiguration config)
{
    var smtpHost  = Environment.GetEnvironmentVariable("BREVO_SMTP_HOST")  ?? "smtp-relay.brevo.com";
    var smtpPort  = int.TryParse(Environment.GetEnvironmentVariable("BREVO_SMTP_PORT"), out var p) ? p : 587;
    var smtpLogin = config["Brevo:SmtpUser"]
                     ?? Environment.GetEnvironmentVariable("BREVO_SMTP_LOGIN")
                     ?? "9f924d001@smtp-brevo.com"; // not secret — Brevo account's fixed SMTP login
    var smtpKey   = config["Brevo:SmtpKey"]    ?? Environment.GetEnvironmentVariable("BREVO_SMTP_KEY");

    var senderEmail    = config["Brevo:SenderEmail"] ?? Environment.GetEnvironmentVariable("BREVO_SENDER_EMAIL");
    var senderName     = Environment.GetEnvironmentVariable("BREVO_SENDER_NAME") ?? "Kozmo Check-ins";
    // Not a secret — a test recipient address, given directly for demo-day verification.
    var recipientEmail = Environment.GetEnvironmentVariable("CHECKIN_TEST_RECIPIENT_EMAIL") ?? "samee_a@optimusbt.net";
    var recipientName  = Environment.GetEnvironmentVariable("CHECKIN_TEST_RECIPIENT_NAME") ?? "Kozmo Demo";

    if (string.IsNullOrWhiteSpace(smtpKey)
        || string.IsNullOrWhiteSpace(senderEmail)
        || string.IsNullOrWhiteSpace(recipientEmail))
    {
        Console.WriteLine("[checkin-transport] Brevo:SmtpKey/SenderEmail (or CHECKIN_TEST_RECIPIENT_EMAIL) " +
                           "not fully configured -> using InAppCheckInTransport (no real email will be sent).");
        return new InAppCheckInTransport();
    }

    Console.WriteLine($"[checkin-transport] Brevo SMTP transport selected — sender={senderEmail}, " +
                       $"recipient={recipientEmail}, host={smtpHost}:{smtpPort}.");
    return new BrevoCheckInTransport(
        smtpHost, smtpPort, smtpLogin, smtpKey, senderEmail, senderName, recipientEmail, recipientName);
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
// ProgramId is accepted for shape-compatibility with a future multi-program design but is
// currently a no-op: the system has exactly one program today (VendorCleanupProgramId, a fixed
// constant in SqliteEntityStore.SaveRegistryVendorAsync) — every KYV-discovered vendor is stamped
// into it regardless of what's passed here. Wiring a real per-request program target would mean
// threading a programId through RegistryWriter/KyvProgramRunner, out of scope for this endpoint
// (which only replaces the Drive-download hop, not the pipeline itself).
record KyvRunLocalRequest(string LocalPath, string? ProgramId = null);

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
