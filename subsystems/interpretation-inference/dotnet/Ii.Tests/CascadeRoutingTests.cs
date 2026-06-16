using Ii.Contracts;
using Ii.Spine;
using Kozmo.Contracts;
using Kozmo.Llm;
using Xunit;

namespace Ii.Tests;

/// <summary>
/// Class R — Cascade routing (B3 STEP 1).
/// Verifies that the rules-first / LLM-residue cascade routes signals correctly.
///
/// R1  Structured signal with matching rule   → ClassificationMethod.Rule, LLM never called
/// R2  HumanReport "body" signal              → ClassificationMethod.Llm, served from committed cache
/// R3  Signal that fails all rules, no "body" → falls to LLM (LlmCacheMissException with empty cache)
///     RED until cascade generalized in ObservationModule (STEP 5)
/// </summary>
public sealed class CascadeRoutingTests
{
    private static readonly Guid CwId = Guid.Parse("eeeeeeee-0001-0000-0000-000000000001");

    private static CachingLlmClient MakeReplayClient() =>
        new CachingLlmClient(TestHelpers.FindLlmCachePath(), recordMode: false);

    // ── R1: structured signal → Rule method ───────────────────────────────────

    [Fact]
    [Trait("Class", "R")]
    public void R1_StructuredSignal_WithMatchingRule_UsesRuleMethod()
    {
        using var h = TestHarness.FreshEngineWithSeed(llm: MakeReplayClient());

        var signal = new Signal(
            Id:           Guid.NewGuid(),
            EntityId:     CwId,
            CustomerId:   Guid.Parse("cccccccc-0000-0000-0000-000000000001"),
            SourceSystem: SourceSystem.MonitoringPlatform,
            ExternalId:   "mon-r1-routing",
            Payload:      new Dictionary<string, object?> { ["uptime_pct"] = 98.5 },
            ObservedAt:   new DateTimeOffset(2026, 6, 10, 8, 0, 0, TimeSpan.Zero),
            ReceivedAt:   new DateTimeOffset(2026, 6, 10, 8, 5, 0, TimeSpan.Zero),
            TraceId:      Guid.NewGuid());

        h.Ingest(signal);

        var beliefs = h.GetBeliefs("cloudwave");
        var belief  = beliefs.FirstOrDefault(b => b.Criterion == "uptime_sla");
        Assert.NotNull(belief);
        Assert.Equal(ClassificationMethod.Rule, belief.ClassificationMethod);
    }

    // ── R2: HumanReport body → Llm method ────────────────────────────────────

    [Fact]
    [Trait("Class", "R")]
    public void R2_HumanReportBody_UsesLlmMethod()
    {
        using var h = TestHarness.FreshEngineWithSeed(llm: MakeReplayClient());

        // Exact same body as Signal 11 in fixtures — cache hit guaranteed
        var signal = new Signal(
            Id:           Guid.NewGuid(),
            EntityId:     CwId,
            CustomerId:   Guid.Parse("cccccccc-0000-0000-0000-000000000001"),
            SourceSystem: SourceSystem.HumanReport,
            ExternalId:   "hr-r2-routing",
            Payload:      new Dictionary<string, object?>
            {
                ["body"] = "Spoke with Cloudwave's account manager today. Adoption is tracking at 30-35% of licensed seats. They mentioned an SLA breach last week that is still being investigated."
            },
            ObservedAt:   new DateTimeOffset(2026, 6, 3, 11, 0, 0, TimeSpan.Zero),
            ReceivedAt:   new DateTimeOffset(2026, 6, 3, 11, 5, 0, TimeSpan.Zero),
            TraceId:      Guid.NewGuid());

        h.Ingest(signal);

        var beliefs   = h.GetBeliefs("cloudwave");
        var llmBelief = beliefs.FirstOrDefault(b => b.ClassificationMethod == ClassificationMethod.Llm);
        Assert.NotNull(llmBelief);
        Assert.Equal(ClassificationMethod.Llm, llmBelief.ClassificationMethod);
    }

    // ── R3: no "body" key, no matching rule → falls to LLM residue ───────────
    // RED until cascade generalized (STEP 5).
    // After fix: LLM is invoked even without "body" → LlmCacheMissException with empty cache.

    [Fact]
    [Trait("Class", "R")]
    public void R3_UnclassifiedSignal_NobodyKey_FallsToLlm()
    {
        var tmp = Path.GetTempFileName();
        File.WriteAllText(tmp, "{}");
        try
        {
            var emptyCacheLlm = new CachingLlmClient(tmp, recordMode: false);
            using var h = TestHarness.FreshEngineWithSeed(llm: emptyCacheLlm);

            // HumanReport with no "body" key — matches no classification rule, no "body" key
            var signal = new Signal(
                Id:           Guid.NewGuid(),
                EntityId:     CwId,
                CustomerId:   Guid.Parse("cccccccc-0000-0000-0000-000000000001"),
                SourceSystem: SourceSystem.HumanReport,
                ExternalId:   "hr-r3-cascade",
                Payload:      new Dictionary<string, object?> { ["note"] = "Some freeform observation without a body key." },
                ObservedAt:   new DateTimeOffset(2026, 6, 10, 10, 0, 0, TimeSpan.Zero),
                ReceivedAt:   new DateTimeOffset(2026, 6, 10, 10, 5, 0, TimeSpan.Zero),
                TraceId:      Guid.NewGuid());

            Assert.Throws<LlmCacheMissException>(() => h.Ingest(signal));
        }
        finally { File.Delete(tmp); }
    }
}
