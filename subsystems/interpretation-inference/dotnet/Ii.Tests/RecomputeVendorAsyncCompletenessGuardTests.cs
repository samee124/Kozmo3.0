using Ii.Completeness;
using Ii.Decay;
using Ii.Index;
using Ii.Observation;
using Ii.Posture;
using Ii.Rubric;
using Ii.Spine;
using Km.Store;
using Kozmo.Contracts;
using Kozmo.Llm;
using Wc.CheckIn;
using Xunit;

namespace Ii.Tests;

/// <summary>
/// Fix #2 from the demo dry-run plan: RecomputeVendorAsync unconditionally awaited
/// CompletenessOrchestrator.RunAsync, which is not internally guarded against
/// LlmCacheMissException. Any of RecomputeVendorAsync's 7 production callers (vendor-file
/// endpoints, VendorFileStageRunner, ProcessCheckInService) would previously propagate that
/// exception straight out — a live click on a KYV vendor's file crashed with a 500, even though
/// the index/posture/meta/management block were already fully computed and renderable.
///
/// Proves the fix mirrors KyvProgramRunner stage 8's per-vendor containment: a completeness
/// failure degrades to "no completeness update this cycle," not a failed recompute.
/// </summary>
public sealed class RecomputeVendorAsyncCompletenessGuardTests
{
    private static readonly Guid VendorId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");

    [Fact]
    public async Task RecomputeVendorAsync_CompletenessFailure_StillReturnsRenderableJudgement()
    {
        var profile  = TestHelpers.LoadProfile();
        using var store = new SqliteEntityStore("Data Source=:memory:");
        var registry = new EntityRegistry();
        registry.Register(VendorId, "Acme Test Vendor, Inc.", renewalDate: null);

        // Seed one real scored belief — RecomputeVendorAsync now returns null for a vendor with
        // zero scored evidence (fix #4), which would short-circuit before ever reaching the
        // completeness guard this test exists to prove.
        await store.AppendBeliefAsync(new Belief(
            Id:            Guid.NewGuid(),
            EntityId:      VendorId,
            Dimension:     Dimension.Operational,
            Criterion:     "sla_uptime",
            Value:         0.90,
            SourceTier:    SourceTier.Verified,
            Confidence:    0.90,
            Freshness:     1.0,
            Derivation:    "test-seed",
            SourceSignals: [],
            Version:       1,
            SupersededBy:  null,
            CreatedAt:     DateTimeOffset.UtcNow,
            TraceId:       Guid.NewGuid()));

        var alwaysThrowLlm = new AlwaysThrowLlm();
        var completeness = new CompletenessOrchestrator(
            new QuestionAnsweringStage(alwaysThrowLlm, profile),
            new GapCheckInStage(),
            new CheckInRepository(store),
            DepthLevel.L1,
            "test@kozmo");

        var facade = new IiFacade(
            new ObservationModule(), new RubricModule(), new IndexModule(),
            new PostureModule(), new DecayEngine(),
            store, profile, registry, completeness: completeness);

        // Must not throw — the whole point of the fix.
        var judgement = await facade.RecomputeVendorAsync(VendorId);

        Assert.NotNull(judgement);
        Assert.NotNull(judgement.Index);
        Assert.NotNull(judgement.Posture);
        Assert.NotNull(judgement.Management);
        Assert.Equal(VendorId, judgement.Index.EntityId);
        Assert.Equal(4, judgement.Index.DimensionScores.Count);
    }

    private sealed class AlwaysThrowLlm : IKozmoLlm
    {
        public Task<LlmResult> CompleteJsonAsync(
            string system, string user, int maxTokens = 500, CancellationToken ct = default) =>
            throw new LlmCacheMissException("simulated unrecorded completeness prompt");
    }
}
