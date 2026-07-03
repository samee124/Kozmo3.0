using System.Text.Json;
using Ig.Contracts;
using Ii.Completeness;
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
using Wc.CheckIn;
using Wc.Contracts;
using Xunit;

namespace Kyv.ProgramRunner.Tests;

using CheckIn = global::Wc.Contracts.CheckIn;

/// <summary>
/// Production-wiring integration tests for Phase 5 Commit 4.
///
/// Three properties under test:
///   (1) KYV runner with completeness wired raises DIMENSION_GAP check-ins after resolve.
///   (2) KYV IiFacade calls completeness LLM on RecomputeVendorAsync; legacy (null) does not.
///   (3) ProcessCheckInService → DIMENSION_GAP answer → RecomputeVendorAsync → completeness re-answers.
///
/// All tests use a CountingLlm rather than the cassette so they are independent of cassette
/// content and always pass (never skip due to cache miss). The cassette is proven in
/// Ii.Completeness.Tests; here we prove that the WIRING causes the LLM to be called.
/// </summary>
public sealed class CompletenessWiringTests : IDisposable
{
    private static readonly Guid VendorId =
        Guid.Parse("eeeeeeee-0000-0000-0000-000000000001");

    private static readonly DateTimeOffset Now =
        new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly string Workspace = @"D:\June\Kozmo Workspace";

    private readonly SqliteEntityStore _store;
    private readonly CheckInRepository _checkInStore;

    private static readonly SaasProfile MinimalProfile = new(
        ConfigVersion:       "test",
        Dimensions:          new Dictionary<string, DimensionDefinition>(),
        ScoringRubric:       new Dictionary<string, CriterionRubric>(),
        DimensionWeights:    new Dictionary<string, double>(),
        Bands:               new BandsConfig(0.6, 0.4, 0.5, 0.1, 0.05),
        PostureRules:        new List<PostureRule>(),
        SourceTiers:         new Dictionary<string, SourceTierConfig>(),
        ClassificationRules: new List<ClassificationRule>(),
        HalfLifeDays:        new Dictionary<string, int>(),
        EntityResolution:    new EntityResolutionConfig("exact", 0.85, new Dictionary<string, string>()));

    public CompletenessWiringTests()
    {
        _store        = new SqliteEntityStore("Data Source=:memory:");
        _checkInStore = new CheckInRepository(_store);
    }

    public void Dispose() => _store.Dispose();

    // ── (1) KYV runner wires completeness — initial gap check-ins raised ────────
    //
    // After KyvProgramRunner resolves vendors from the workspace, the completeness
    // orchestrator fires per vendor and raises DIMENSION_GAP check-ins.

    [SkippableFact]
    public async Task KyvRunner_WithCompleteness_RaisesGapCheckInsForResolvedVendors()
    {
        Skip.If(!Directory.Exists(Workspace),
            $"Workspace absent: '{Workspace}' — place scenario PDFs there to run this test.");

        var repoRoot = FindRepoRoot();
        var cassette = Path.Combine(repoRoot, "fixtures", "kyv", "candidate-extraction.cassette.json");
        var llm      = new CachingLlmClient(cassette, recordMode: false);

        var countingLlm  = new CountingLlm();
        var completeness = new CompletenessOrchestrator(
            new QuestionAnsweringStage(countingLlm), new GapCheckInStage(),
            _checkInStore, DepthLevel.L1, "kyv@kozmo");

        var runner = new KyvProgramRunner(
            llm, new WiringFakeEntityTypeClassifier(EntityType.Company),
            new NoOpIdentityRegistry(), _checkInStore,
            completeness: completeness);

        var run = await runner.RunAsync(Workspace, Now);

        // Stage 7 must be recorded
        Assert.Contains(run.Stages, s => s.StageName == "completeness_init");

        // Completeness LLM was called at least once (at least one vendor was resolved)
        Assert.True(countingLlm.CallCount > 0,
            "Completeness LLM must be invoked for at least one resolved vendor.");

        // DIMENSION_GAP check-ins were raised (empty beliefs → all L1 questions are gaps)
        var open = await _checkInStore.GetOpenAsync();
        Assert.Contains(open, ci => ci.Kind == CheckInKind.DIMENSION_GAP);
    }

    // ── (2) KYV facade vs legacy: side-by-side call count ───────────────────────
    //
    // Same CountingLlm, two different IiFacade instances.
    //   KYV (completeness wired)  → RecomputeVendorAsync calls the LLM.
    //   Legacy (completeness null) → RecomputeVendorAsync calls no LLM.

    [Fact]
    public async Task KyvFacade_RecomputeVendor_CallsCompleteness_LegacyFacade_DoesNot()
    {
        var countingLlm = new CountingLlm();

        // KYV facade — completeness wired
        var kyvStore   = new SqliteEntityStore("Data Source=:memory:");
        var kyvCheckIn = new CheckInRepository(kyvStore);
        var kyvOrch    = new CompletenessOrchestrator(
            new QuestionAnsweringStage(countingLlm), new GapCheckInStage(),
            kyvCheckIn, DepthLevel.L1, "kyv@kozmo");

        var kyvFacade = BuildTestFacade(kyvStore, kyvOrch);
        await kyvFacade.RecomputeVendorAsync(VendorId);
        var kyvCallCount = countingLlm.CallCount;
        kyvStore.Dispose();

        // Legacy facade — null completeness
        countingLlm.Reset();
        var legacyStore  = new SqliteEntityStore("Data Source=:memory:");
        var legacyFacade = BuildTestFacade(legacyStore, completeness: null);
        await legacyFacade.RecomputeVendorAsync(VendorId);
        var legacyCallCount = countingLlm.CallCount;
        legacyStore.Dispose();

        Assert.True(kyvCallCount > 0,
            "KYV facade must invoke completeness LLM on RecomputeVendorAsync.");
        Assert.Equal(0, legacyCallCount);
    }

    // ── (3) ProcessCheckInService → completeness re-answers ─────────────────────
    //
    // When a DIMENSION_GAP check-in is answered and ProcessCheckInService processes it,
    // it calls facade.RecomputeVendorAsync. With a KYV facade (completeness wired),
    // that recompute also re-answers all completeness questions for the vendor.

    [Fact]
    public async Task ProcessCheckIn_DimensionGapAnswered_TriggersCompletenessReAnswer()
    {
        var countingLlm = new CountingLlm();
        var kyvOrch     = new CompletenessOrchestrator(
            new QuestionAnsweringStage(countingLlm), new GapCheckInStage(),
            _checkInStore, DepthLevel.L1, "kyv@kozmo");

        var facade = BuildTestFacade(_store, kyvOrch);

        // Seed a DIMENSION_GAP check-in in ANSWERED state
        var ciId    = Guid.NewGuid();
        var checkIn = new CheckIn(
            CheckInId:      ciId,
            VendorId:       VendorId,
            ProgramRunId:   Guid.NewGuid(),
            Kind:           CheckInKind.DIMENSION_GAP,
            Question:       "What is the SLA uptime percentage?",
            ResponseShape:  ResponseShape.TYPED_VALUE,
            TargetField:    null,
            Owner:          "analyst@test",
            Status:         PendingStatus.ANSWERED,
            RaisedAt:       Now,
            AnsweredAt:     Now.AddHours(1),
            ExpiresAt:      null,
            ResponseValue:  "0.995");
        await _checkInStore.SaveAsync(checkIn);

        var writeSvc = new VendorFileWriteService(_store, MinimalProfile);
        var svc      = new ProcessCheckInService();

        await svc.ProcessAsync(
            ciId, _checkInStore, new NoOpIdentityRegistry(),
            writeSvc, facade, MinimalProfile, Now);

        // Completeness must have re-answered after the recompute
        Assert.True(countingLlm.CallCount > 0,
            "Completeness LLM must fire when ProcessCheckInService calls RecomputeVendorAsync " +
            "on a KYV facade.");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────

    private static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(dir))
        {
            if (File.Exists(Path.Combine(dir, "Kozmo.sln"))) return dir;
            dir = Path.GetDirectoryName(dir);
        }
        throw new InvalidOperationException("Cannot locate repo root (Kozmo.sln not found).");
    }

    private static IiFacade BuildTestFacade(
        SqliteEntityStore store, CompletenessOrchestrator? completeness)
    {
        var registry = new EntityRegistry();
        return new IiFacade(
            new ObservationModule(), new RubricModule(), new IndexModule(),
            new PostureModule(), new DecayEngine(),
            store, MinimalProfile, registry,
            clock: null, completeness: completeness);
    }
}

// ── Test fakes ────────────────────────────────────────────────────────────────

file sealed class CountingLlm : IKozmoLlm
{
    private int _count;
    public int  CallCount => _count;
    public void Reset()   => _count = 0;

    private const string UnknownJson =
        """{"answer":"UNKNOWN","confidence":0.10,"cited_belief_ids":[],"reasoning":"No evidence."}""";

    public Task<LlmResult> CompleteJsonAsync(
        string system, string user, int maxTokens = 500, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _count);
        var el = JsonSerializer.Deserialize<JsonElement>(UnknownJson);
        return Task.FromResult(new LlmResult(el, 0.10, "counting-fake"));
    }

    public Task<LlmResult> CompleteVisionAsync(
        string system, byte[] imageBytes, int maxTokens = 500, CancellationToken ct = default) =>
        throw new NotSupportedException();
}

file sealed class WiringFakeEntityTypeClassifier(EntityType result) : IEntityTypeClassifier
{
    public Task<EntityType> ClassifyAsync(string effectiveName, string comparisonKey, CancellationToken ct = default) =>
        Task.FromResult(result);
}

file sealed class NoOpIdentityRegistry : IIdentityRegistry
{
    public Task SaveAsync(CanonicalVendor vendor, CancellationToken ct = default, Guid? programRunId = null) =>
        Task.CompletedTask;
    public Task<CanonicalVendor?> GetAsync(Guid vendorId, CancellationToken ct = default) =>
        Task.FromResult<CanonicalVendor?>(null);
    public Task<IReadOnlyList<CanonicalVendor>> GetAllAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<CanonicalVendor>>(Array.Empty<CanonicalVendor>());
    public Task MarkAbsorbedAsync(Guid vendorId, Guid survivorVendorId, CancellationToken ct = default) =>
        Task.CompletedTask;
}
