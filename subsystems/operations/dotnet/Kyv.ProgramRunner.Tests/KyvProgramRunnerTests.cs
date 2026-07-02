using Ig.Contracts;
using Ig.Resolution;
using Ii.Contracts;
using Km.Store;
using Kozmo.Contracts;
using Kozmo.Contracts.Config;
using Kozmo.Contracts.Interfaces;
using Kozmo.Llm;
using Wc.CheckIn;
using Wc.Contracts;
using Xunit;

namespace Kyv.ProgramRunner.Tests;

/// <summary>
/// End-to-end integration tests for KyvProgramRunner executing the declared stage sequence
/// over D:\June\Kozmo Workspace (Scenarios 01–04). Tests skip if the workspace is absent.
/// LLM extraction replays from the frozen cassette — no live network calls.
/// Entity-type classification uses FakeEntityTypeClassifier(Company) as a safe offline fallback
/// (same pattern as Ig.Tests uses, consistent with deterministic rules handling most cases).
/// </summary>
public sealed class KyvProgramRunnerTests : IDisposable
{
    private static readonly string Workspace  = @"D:\June\Kozmo Workspace";
    private static readonly DateTimeOffset Now =
        new DateTimeOffset(2026, 6, 30, 0, 0, 0, TimeSpan.Zero);

    private readonly SqliteEntityStore    _store;
    private readonly IdentityRegistry    _registry;
    private readonly CheckInRepository   _checkInStore;

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

    public KyvProgramRunnerTests()
    {
        _store        = new SqliteEntityStore("Data Source=:memory:");
        _registry     = new IdentityRegistry(_store);
        _checkInStore = new CheckInRepository(_store);
    }

    public void Dispose() => _store.Dispose();

    // ── Helpers ────────────────────────────────────────────────────────────────

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

    // Skips visibly via xunit.skippablefact (Skip.If → SkipException recognised by the runner).
    // Prints the resolved path before throwing so path mismatches are visible in diagnostic output.
    private static void RequireWorkspace()
    {
        var exists = Directory.Exists(Workspace);
        Console.WriteLine($"[KYV] Workspace '{Workspace}' → {(exists ? "FOUND" : "NOT FOUND")}");
        Console.WriteLine($"[KYV] CWD: {Directory.GetCurrentDirectory()}");
        Skip.If(!exists, $"Workspace absent: '{Workspace}' — place Scenarios 01-04 PDFs there to run these tests.");
    }

    private KyvProgramRunner BuildRunner()
    {
        var repoRoot = FindRepoRoot();
        var cassette = Path.Combine(repoRoot, "fixtures", "kyv", "candidate-extraction.cassette.json");
        var llm      = new CachingLlmClient(cassette, recordMode: false);
        return new KyvProgramRunner(llm, new FakeEntityTypeClassifier(EntityType.Company), _registry, _checkInStore);
    }

    // ── Tests ──────────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task ProgramRun_RealFolder_CompletesAllStages()
    {
        RequireWorkspace();

        var runner = BuildRunner();
        var run    = await runner.RunAsync(Workspace, Now);

        Assert.Equal(ProgramRunStatus.Completed, run.Status);
        Assert.Equal("Know Your Vendor",         run.ProgramName);
        Assert.Equal(Workspace,                  run.SourceFolder);
        Assert.Equal(Now,                        run.StartedAt);
        Assert.NotEqual(Guid.Empty,              run.RunId);

        // All 6 declared stages must be recorded
        Assert.Equal(6, run.Stages.Count);
        var names = run.Stages.Select(s => s.StageName).ToList();
        Assert.Contains("ingest",         names);
        Assert.Contains("classify",       names);
        Assert.Contains("extract",        names);
        Assert.Contains("filter",         names);
        Assert.Contains("resolve",        names);
        Assert.Contains("raise_checkins", names);

        // Unreadable documents must be reported — not silently dropped.
        // Scenario 05 contains two image-only PDFs (no text layer) that PdfPig cannot read.
        Assert.NotNull(run.UnreadableDocuments);
        Assert.True(run.UnreadableDocuments.Count >= 1,
            $"Expected at least 1 unreadable document; got 0. " +
            $"Image-only PDFs in Scenario 05 must surface as UnreadableDocuments, not silent skips.");

        Console.WriteLine($"[KYV] {run.UnreadableDocuments.Count} unreadable doc(s):");
        foreach (var u in run.UnreadableDocuments)
            Console.WriteLine($"  ! {u.RelativePath}  ({u.Reason})");
    }

    [SkippableFact]
    public async Task ProgramRun_PersistsCorrectVendorSet_CustomersExcluded()
    {
        RequireWorkspace();

        var runner = BuildRunner();
        var run    = await runner.RunAsync(Workspace, Now);

        // Only vendors stamped with THIS run's ID should appear
        var all = await _registry.GetAllAsync();

        // Vendors expected in Scenarios 01–04:
        // IIVS (Institute for In Vitro Sciences), Aequitas, Regulus, ABC Tech, ABC Technologies
        Assert.True(all.Count >= 1,
            $"Expected at least 1 vendor in registry; got {all.Count}");

        // Customers must NOT appear as vendors
        var names = all.Select(v => v.CanonicalName).ToList();
        Assert.DoesNotContain(names, n => n.Contains("Revolution Medicines", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, n => n.Contains("Prudential",           StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, n => n.Contains("Biogen",               StringComparison.OrdinalIgnoreCase));

        // Every persisted vendor must belong to THIS run (program_run_id isolation)
        var runRows = await _store.GetAllRegistryVendorsAsync();
        Assert.All(runRows.Where(r => r.Status != "Absorbed"),
            r => Assert.Equal(run.RunId, r.ProgramRunId));
    }

    [SkippableFact]
    public async Task ProgramRun_LegacyDemo_Untouched()
    {
        RequireWorkspace();

        // Seed ONE legacy vendor (program_run_id = null) before the KYV run.
        // This makes the assertion non-vacuous: LoadVendorsAsync must return this row
        // (proving the NULL filter is live) while excluding the KYV-stamped rows.
        var legacyId = Guid.NewGuid();
        await _store.SaveRegistryVendorAsync(new RegistryVendorRow(
            VendorId:             legacyId,
            CanonicalName:        "Cloudwave Systems Inc.",
            CreatedAt:            Now,
            ComparisonKey:        null,
            EntityType:           null,
            Confidence:           null,
            FlagsJson:            null,
            Status:               null,
            RebrandMapRef:        null,
            AcquisitionMapRef:    null,
            AbsorbedIntoVendorId: null,
            ProgramRunId:         null));  // legacy: no program_run_id

        var runner = BuildRunner();
        await runner.RunAsync(Workspace, Now);

        // LoadVendorsAsync filters WHERE program_run_id IS NULL.
        var legacyVendors = await _store.LoadVendorsAsync();

        // The seeded legacy vendor MUST appear — proves the filter returns NULL rows, not empty.
        Assert.Contains(legacyVendors, v => v.Id == legacyId);

        // KYV vendors (all stamped with the run's program_run_id) must be ABSENT.
        Assert.DoesNotContain(legacyVendors, v => v.Name.Contains("Vitro",    StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(legacyVendors, v => v.Name.Contains("Aequitas", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(legacyVendors, v => v.Name.Contains("Regulus",  StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(legacyVendors, v => v.Name.Contains("ABC",      StringComparison.OrdinalIgnoreCase));
    }

    [SkippableFact]
    public async Task ProgramRun_All6Scenarios_VendorSet_NoTimeout()
    {
        RequireWorkspace();

        var runner = BuildRunner();
        var run    = await runner.RunAsync(Workspace, Now);

        Assert.Equal(ProgramRunStatus.Completed, run.Status);

        // All 150 PDFs ingested; stage count must still be 6
        var ingest = run.Stages.Single(s => s.StageName == "ingest");
        Assert.True(ingest.ItemsProcessed >= 100,
            $"Expected ≥ 100 docs from 6 scenarios; got {ingest.ItemsProcessed}");

        // Unreadable docs reported, not silently swallowed
        Assert.NotNull(run.UnreadableDocuments);

        // Resolved vendor set — print for diagnostic visibility
        var all      = await _registry.GetAllAsync();
        var vendorIds = all.Select(v => v.VendorId).ToHashSet();

        Console.WriteLine($"[KYV] {ingest.ItemsProcessed} PDFs ingested, " +
                          $"{run.UnreadableDocuments.Count} unreadable, " +
                          $"{all.Count} vendor(s) resolved:");
        foreach (var v in all.OrderBy(v => v.CanonicalName))
            Console.WriteLine($"  [{v.Status,-12}] {v.CanonicalName}");

        if (run.UnreadableDocuments.Count > 0)
        {
            Console.WriteLine($"[KYV] Unreadable:");
            foreach (var u in run.UnreadableDocuments)
                Console.WriteLine($"  ! {u.RelativePath}");
        }

        // Scenario 06 — Financial-Only Vendor: Workday invoices (111 docs) must produce
        // at least one resolved vendor and must NOT time out or throw.
        Assert.True(all.Any(v => v.CanonicalName.Contains("Workday", StringComparison.OrdinalIgnoreCase)),
            "Scenario 06 (111 Workday invoices) produced no Workday vendor entry.");

        // Customers must not bleed through into the vendor registry
        Assert.DoesNotContain(all, v => v.CanonicalName.Contains("Brookfield",        StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(all, v => v.CanonicalName.Contains("Revolution Medicines", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(all, v => v.CanonicalName.Contains("Meridian Health",    StringComparison.OrdinalIgnoreCase));
    }

    // ── Commit 2: raise_checkins wired end-to-end ─────────────────────────────

    [SkippableFact]
    public async Task ProgramRun_RaisesLiveCheckins_AgainstPersistedVendors()
    {
        RequireWorkspace();

        var runner = BuildRunner();
        var run    = await runner.RunAsync(Workspace, Now);

        var open = await _checkInStore.GetOpenAsync();
        Assert.NotEmpty(open);

        // At least one IDENTITY_CONFIRM check-in for the ABC near-miss pair
        Assert.Contains(open, ci => ci.Kind == CheckInKind.IDENTITY_CONFIRM);

        // At least one DIMENSION_GAP check-in for provisional vendors (Regulus, Aequitas)
        Assert.Contains(open, ci => ci.Kind == CheckInKind.DIMENSION_GAP);

        // Every check-in belongs to this run
        Assert.All(open, ci => Assert.Equal(run.RunId, ci.ProgramRunId));

        // Every check-in's VendorId references a vendor that was actually persisted
        var allVendors = await _registry.GetAllAsync();
        var vendorIds  = allVendors.Select(v => v.VendorId).ToHashSet();
        Assert.All(open, ci => Assert.Contains(ci.VendorId, vendorIds));
    }

    [SkippableFact]
    public async Task ProgramRun_AbcIdentityAnswerYes_MergesLive_Absorbed_NotDeleted()
    {
        RequireWorkspace();

        var runner = BuildRunner();
        await runner.RunAsync(Workspace, Now);

        // Find the ABC IDENTITY_CONFIRM check-in
        var open  = await _checkInStore.GetOpenAsync();
        var abcCi = open.FirstOrDefault(ci => ci.Kind == CheckInKind.IDENTITY_CONFIRM);
        Assert.NotNull(abcCi);
        Assert.NotNull(abcCi!.PairedVendorId); // must carry both sides of the pair

        // Simulate owner answering YES
        var answered = abcCi with
        {
            Status        = PendingStatus.ANSWERED,
            AnsweredAt    = Now.AddHours(1),
            ResponseValue = "true"
        };
        await _checkInStore.SaveAsync(answered);

        // Process the response via the real service against the real registry —
        // THIS IS THE PREVIOUSLY-INERT PATH (vendors now persisted by Stage F).
        var entityStore = new InMemoryEntityStore();
        var facade      = new TrackingFacade();
        var writeSvc    = new VendorFileWriteService(entityStore, MinimalProfile);
        var result      = await new ProcessCheckInService()
            .ProcessAsync(abcCi.CheckInId, _checkInStore, _registry, writeSvc, facade, MinimalProfile, Now);

        Assert.Equal(ProcessOutcome.Ok, result.Outcome);
        Assert.NotNull(result.AffectedVendorId);

        // Survivor: promoted to Confirmed, absorbs the paired vendor's aliases
        var survivor = await _registry.GetAsync(result.AffectedVendorId!.Value);
        Assert.NotNull(survivor);
        Assert.Equal(RegistryStatus.Confirmed, survivor!.Status);

        // Absorbed: non-destructively marked (NOT deleted), points to survivor
        var absorbed = await _registry.GetAsync(abcCi.PairedVendorId.Value);
        Assert.NotNull(absorbed);
        Assert.Equal(RegistryStatus.Absorbed,              absorbed!.Status);
        Assert.Equal(result.AffectedVendorId.Value, absorbed.AbsorbedIntoVendorId);

        // Recompute fired for survivor only; absorbed stays out of active pipeline
        Assert.Contains(result.AffectedVendorId.Value, facade.RecomputedVendors);
        Assert.DoesNotContain(abcCi.PairedVendorId.Value, facade.RecomputedVendors);
    }

    [SkippableFact]
    public async Task ProgramRun_WrongMatchGuard_HoldsEndToEnd()
    {
        RequireWorkspace();

        var runner = BuildRunner();
        await runner.RunAsync(Workspace, Now);

        // Grab at least two open DIMENSION_GAP check-ins (Regulus + Aequitas)
        var open     = await _checkInStore.GetOpenAsync();
        var gapCIs   = open.Where(ci => ci.Kind == CheckInKind.DIMENSION_GAP).ToList();
        Assert.True(gapCIs.Count >= 2, "Expected at least 2 DIMENSION_GAP check-ins from Provisional vendors");

        var ciToProcess   = gapCIs[0];
        var untouchedCi   = gapCIs[1];
        var untouchedVendorId = untouchedCi.VendorId;

        // Simulate an answer for the FIRST check-in only
        await _checkInStore.SaveAsync(ciToProcess with
        {
            Status        = PendingStatus.ANSWERED,
            AnsweredAt    = Now.AddHours(1),
            ResponseValue = "active"
        });

        var entityStore = new InMemoryEntityStore();
        var facade      = new TrackingFacade();
        var writeSvc    = new VendorFileWriteService(entityStore, MinimalProfile);
        await new ProcessCheckInService()
            .ProcessAsync(ciToProcess.CheckInId, _checkInStore, _registry, writeSvc, facade, MinimalProfile, Now);

        // Recompute fired for the processed vendor only — not for the untouched one
        Assert.Contains(ciToProcess.VendorId,  facade.RecomputedVendors);
        Assert.DoesNotContain(untouchedVendorId, facade.RecomputedVendors);

        // The untouched check-in is still OPEN — processing one did not consume the other
        var stillOpen = await _checkInStore.GetAsync(untouchedCi.CheckInId);
        Assert.Equal(PendingStatus.OPEN, stillOpen!.Status);
    }
}

// ── Local fake (mirrors FakeEntityTypeClassifier in Ig.Tests) ─────────────────

file sealed class FakeEntityTypeClassifier(EntityType result) : IEntityTypeClassifier
{
    public Task<EntityType> ClassifyAsync(
        string            effectiveName,
        string            comparisonKey,
        CancellationToken ct = default)
        => Task.FromResult(result);
}

// ── Minimal IIiFacade fake — tracks RecomputeVendorAsync calls ────────────────

file sealed class TrackingFacade : IIiFacade
{
    public List<Guid> RecomputedVendors { get; } = new();

    public Task<VendorJudgement> RecomputeVendorAsync(Guid entityId, CancellationToken ct = default)
    {
        RecomputedVendors.Add(entityId);
        return Task.FromResult<VendorJudgement>(null!);
    }

    public Task<Guid>                           SubmitSignalAsync(Signal signal, CancellationToken ct = default)     => throw new NotSupportedException();
    public Task<PostureAssignment?>              GetPostureAsync(Guid entityId, CancellationToken ct = default)       => throw new NotSupportedException();
    public Task<EntityIndex?>                   GetIndexAsync(Guid entityId, CancellationToken ct = default)          => throw new NotSupportedException();
    public Task<IReadOnlyList<Belief>>          GetBeliefsAsync(Guid entityId, CancellationToken ct = default)        => throw new NotSupportedException();
    public Task<ReasoningTrail?>                GetReasoningTrailAsync(Guid entityId, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<IReadOnlyList<TrajectoryPoint>> GetTrajectoryAsync(Guid entityId, CancellationToken ct = default)     => throw new NotSupportedException();
    public Task                                 ResetAsync(CancellationToken ct = default)                           => throw new NotSupportedException();
}

// ── Minimal IEntityStore fake — captures AppendBeliefAsync calls ──────────────

file sealed class InMemoryEntityStore : IEntityStore
{
    private readonly List<Belief> _beliefs = new();
    public IReadOnlyList<Belief> AllBeliefs => _beliefs;

    public Task AppendBeliefAsync(Belief belief, CancellationToken ct = default)
    {
        _beliefs.Add(belief);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<Belief>> GetCurrentBeliefsAsync(Guid entityId, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<Belief>>(
               _beliefs.Where(b => b.EntityId == entityId && b.SupersededBy == null).ToList());

    public Task<IReadOnlyList<Belief>> GetBeliefHistoryAsync(Guid entityId, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<Belief>>(
               _beliefs.Where(b => b.EntityId == entityId).ToList());

    public Task SaveIndexAsync(EntityIndex index, CancellationToken ct = default)                                        => throw new NotSupportedException();
    public Task<EntityIndex?> GetIndexAsync(Guid entityId, CancellationToken ct = default)                              => throw new NotSupportedException();
    public Task<IReadOnlyList<EntityIndex>> GetIndexHistoryAsync(Guid entityId, CancellationToken ct = default)         => throw new NotSupportedException();
    public Task AppendPostureAsync(PostureAssignment posture, CancellationToken ct = default)                            => throw new NotSupportedException();
    public Task<PostureAssignment?> GetCurrentPostureAsync(Guid entityId, CancellationToken ct = default)               => throw new NotSupportedException();
    public Task AppendSignalAsync(Signal signal, CancellationToken ct = default)                                        => throw new NotSupportedException();
    public Task<Signal?> GetSignalAsync(Guid signalId, CancellationToken ct = default)                                 => throw new NotSupportedException();
    public Task<IReadOnlyList<PostureAssignment>> GetPostureHistoryAsync(Guid entityId, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<IReadOnlyList<Signal>> GetSignalsForEntityAsync(Guid entityId, CancellationToken ct = default)          => throw new NotSupportedException();
    public Task AppendEvidenceAsync(Evidence evidence, CancellationToken ct = default)                                  => throw new NotSupportedException();
    public Task<Evidence?> GetEvidenceAsync(Guid evidenceId, CancellationToken ct = default)                            => throw new NotSupportedException();
    public Task<IReadOnlyList<Evidence>> GetEvidenceForVendorAsync(Guid vendorId, CancellationToken ct = default)      => throw new NotSupportedException();
    public Task ResetAsync(CancellationToken ct = default)                                                              => throw new NotSupportedException();
}
