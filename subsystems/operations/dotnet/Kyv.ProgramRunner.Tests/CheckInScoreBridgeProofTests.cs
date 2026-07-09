using Ig.Contracts;
using Ig.Resolution;
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
using Wc.CheckIn;
using Wc.Contracts;
using Xunit;

namespace Kyv.ProgramRunner.Tests;

/// <summary>
/// E2 Step 1-2 — end-to-end proof that answering a real check-in makes a real vendor's dimension
/// assessable and the Index computes. Uses the SAME real IIVS belief set
/// RealDocumentCompletenessProofTests proves (real documents -> real extracted beliefs, via
/// RealVendorBeliefFixture/KyvProgramRunner), then drives the REAL check-in engine
/// (GapCheckInStage -> AnswerCheckInService -> ProcessCheckInService) against it — no synthetic
/// vendor, no synthetic beliefs, no identity-resolution changes.
///
/// Baseline (asserted below, matching the live kozmo-demo.db state this proof was diagnosed
/// against): IIVS's real documents produce ONLY Financial beliefs (payment_terms, invoice_amount),
/// and both are catalogue "structural" claims, so VendorFileWriteService forces Confidence=0 on
/// write. Zero dimensions have any scoreable evidence -> RecomputeVendorAsync returns null. This is
/// the "completeness rises, the score never exists" gap the E2 spec names.
///
/// saas.op.l1.2 ("What is the vendor's contracted uptime SLA percentage?") is the real, currently
/// open L1 check-in for IIVS (confirmed live) — bound to claim_key "sla_uptime" ->
/// rubric_criterion "uptime_sla" (SaasQuestionBank). Answering it with a realistic value (99.9%)
/// must write a Confirmed-tier (0.65), Operational, banded (1.00) belief — not the pre-fix
/// Financial/1.0 present-field write — and that alone must flip the Index from null to computed.
/// </summary>
public sealed class CheckInScoreBridgeProofTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 30, 0, 0, 0, TimeSpan.Zero);
    private static readonly FixedClock TestClock = new(Now);
    private static readonly Guid RealIivsVendorId = Guid.Parse("d0000000-0000-0000-0000-000000000001");

    private static readonly string Workspace = @"D:\June\Kozmo Workspace";
    private static readonly string RepoRoot  = FindRepoRoot();

    [SkippableFact]
    public async Task RealIivs_AnsweredUptimeCheckIn_BridgesToScoredOperationalBelief_IndexComputes()
    {
        Skip.If(!Directory.Exists(Workspace), $"Workspace absent: '{Workspace}'.");

        var candidateCassette = Path.Combine(RepoRoot, "fixtures", "kyv", "candidate-extraction.cassette.json");
        var beliefCassette    = Path.Combine(RepoRoot, "fixtures", "kyv", "belief-extraction.cassette.json");
        Skip.If(!File.Exists(candidateCassette) || !File.Exists(beliefCassette),
            "Extraction cassettes not recorded yet.");

        var profile = CatalogueTestHelper.LoadProfile();

        // ── Real IIVS beliefs — real documents, real extraction, real persistence path ─────────
        var beliefs = await RealVendorBeliefFixture.BuildAsync(
            Workspace, candidateCassette, beliefCassette, profile, "Vitro", RealIivsVendorId, Now);
        Assert.NotNull(beliefs);
        Assert.NotEmpty(beliefs!);

        // ── Baseline, step 1: every real belief is Financial, Confidence 0.0 ────────────────────
        Assert.All(beliefs!, b => Assert.Equal(0.0, b.Confidence));
        Assert.All(beliefs!, b => Assert.Equal(Dimension.Financial, b.Dimension));

        using var store = new SqliteEntityStore("Data Source=:memory:");
        foreach (var b in beliefs!)
            await store.AppendBeliefAsync(b);

        var registry = new EntityRegistry();
        registry.Register(RealIivsVendorId, "Institute for In Vitro Sciences, Inc.");
        var facade = new IiFacade(
            new ObservationModule(), new RubricModule(), new IndexModule(),
            new PostureModule(), new DecayEngine(),
            store, profile, registry, TestClock);

        // ── Baseline, step 2: Index is null — no dimension has any scored evidence ──────────────
        var baseline = await facade.RecomputeVendorAsync(RealIivsVendorId);
        Assert.Null(baseline);

        // ── Raise the REAL, currently-open L1 gap via the REAL (fixed) GapCheckInStage ──────────
        // saas.op.l1.2 is the real open check-in for IIVS today (confirmed against kozmo-demo.db).
        var checkInStore = new CheckInRepository(store);
        var raised = await new GapCheckInStage().RaiseAsync(
            RealIivsVendorId,
            gapQuestionIds:  ["saas.op.l1.2"],
            allQuestions:    SaasQuestionBank.All,
            permanentGapIds: new HashSet<string>(),
            checkInStore:    checkInStore,
            owner:           "kyv@kozmo",
            runId:           Guid.NewGuid(),
            now:             Now);

        var checkIn = Assert.Single(raised);
        Assert.Equal("sla_uptime", checkIn.TargetClaimKey); // the binding is live on the raised check-in

        // ── Answer it with a realistic contracted-SLA value ─────────────────────────────────────
        var answer = await new AnswerCheckInService().AnswerAsync(
            checkIn.CheckInId, "99.9", Now, checkInStore);
        Assert.Equal(AnswerOutcome.Ok, answer.Outcome);

        // ── Process it: writes the belief, then calls facade.RecomputeVendorAsync internally ────
        var writeService = new VendorFileWriteService(store, profile);
        var processed = await new ProcessCheckInService().ProcessAsync(
            checkIn.CheckInId, checkInStore, new IdentityRegistry(store),
            writeService, facade, profile, Now);
        Assert.Equal(ProcessOutcome.Ok, processed.Outcome);

        // ── Step-by-step proof, real output ──────────────────────────────────────────────────────

        // 1. A Confirmed-tier (0.65), Operational, banded belief was written — not the pre-fix
        //    Financial/1.0 present-field write.
        var current  = await store.GetCurrentBeliefsAsync(RealIivsVendorId);
        var opBelief = Assert.Single(current, b => b.ClaimKey == "sla_uptime");
        Assert.Equal(Dimension.Operational, opBelief.Dimension);
        Assert.Equal("sla_uptime", opBelief.Criterion);
        Assert.Equal(SourceTier.Confirmed, opBelief.SourceTier);
        Assert.Equal(0.65, opBelief.Confidence);
        Assert.Equal(1.00, opBelief.Value); // 99.9% -> uptime_sla band [99.5,100] -> 1.00

        // 2. Confidence > 0 — clears RubricModule.ScoreDimension's filter.
        Assert.True(opBelief.Confidence > 0);

        // 3. Provenance drills to this exact check-in answer (id + human-readable derivation).
        Assert.NotNull(opBelief.Provenance);
        Assert.Equal(checkIn.CheckInId, opBelief.Provenance!.EvidenceId);
        Assert.Equal($"Check-in answer to \"{checkIn.Question}\": 99.9", opBelief.Derivation);

        // 4/5/6. RubricModule scores Operational; Index is no longer null; a real Band + Stance.
        var judgement = await facade.RecomputeVendorAsync(RealIivsVendorId);
        Assert.NotNull(judgement);

        var opScore = judgement!.Index.DimensionScores[Dimension.Operational];
        Assert.Contains(opBelief.Id, opScore.ContributingBeliefIds);
        Assert.Equal(1.00, opScore.Score);

        // Golden re-pin (deliberate versioned event — IIVS goes null -> computed): the real,
        // computed Index for IIVS after this single answered check-in. Operational scores 1.00
        // (weight 0.25); Financial/Experiential/Strategic still have no scored evidence and
        // impute the neutral 0.5 at their own weights — composite = (1.00 + 0.5+0.5+0.5)*0.25 =
        // 0.625. ConfidenceFloor = 0.65 (only Operational has a contributing belief). 0.625 is
        // below profile.Bands.HealthyMin, so Band = AtRisk, driven by composite (not the
        // worst-dimension floor) — and PostureModule assigns Renegotiate for AtRisk. A real
        // vendor that was "not assessed" a moment ago now has a real, auditable posture.
        Assert.Equal(0.625, judgement.Index.Composite);
        Assert.Equal(0.65,  judgement.Index.ConfidenceFloor);
        Assert.Equal(Band.AtRisk, judgement.Index.Band);
        Assert.Equal("composite", judgement.Index.BandDrivenBy);
        Assert.Equal(Stance.Renegotiate, judgement.Posture.Stance);

        // 7. Completeness reflects the new belief too (ManagementBlock.Completeness > baseline's
        //    implicit 0) — same ClaimKey write, both RubricModule and completeness read it.
        Assert.True(judgement.Management.Completeness > 0);
        Assert.True(judgement.Management.FilledCount > 0);

        // The bound question itself, confirmed live in the real question bank.
        var questions     = QuestionSelector.Select(SaasQuestionBank.Category, DepthLevel.L1);
        var boundQuestion = questions.Single(q => q.Id == "saas.op.l1.2");
        Assert.Equal("sla_uptime", boundQuestion.TargetClaimKey);
    }

    [SkippableFact]
    public async Task UnansweredVendor_StillShowsNotAssessed_NoFalsePositive()
    {
        Skip.If(!Directory.Exists(Workspace), $"Workspace absent: '{Workspace}'.");
        var candidateCassette = Path.Combine(RepoRoot, "fixtures", "kyv", "candidate-extraction.cassette.json");
        var beliefCassette    = Path.Combine(RepoRoot, "fixtures", "kyv", "belief-extraction.cassette.json");
        Skip.If(!File.Exists(candidateCassette) || !File.Exists(beliefCassette),
            "Extraction cassettes not recorded yet.");

        var profile = CatalogueTestHelper.LoadProfile();
        var beliefs = await RealVendorBeliefFixture.BuildAsync(
            Workspace, candidateCassette, beliefCassette, profile, "Vitro", RealIivsVendorId, Now);
        Assert.NotNull(beliefs);

        using var store = new SqliteEntityStore("Data Source=:memory:");
        foreach (var b in beliefs!)
            await store.AppendBeliefAsync(b);

        var registry = new EntityRegistry();
        registry.Register(RealIivsVendorId, "Institute for In Vitro Sciences, Inc.");
        var facade = new IiFacade(
            new ObservationModule(), new RubricModule(), new IndexModule(),
            new PostureModule(), new DecayEngine(),
            store, profile, registry, TestClock);

        // No check-in raised, none answered — the vendor must still be "not assessed."
        var judgement = await facade.RecomputeVendorAsync(RealIivsVendorId);
        Assert.Null(judgement);
    }

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
}
