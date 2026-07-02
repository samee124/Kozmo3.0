using System.Text.Json;
using Ii.Contracts;
using Ii.Spine;
using Kozmo.Contracts;
using Kozmo.Llm;
using Xunit;

namespace Ii.Tests;

/// <summary>
/// Class L — LLM integration-gate slice (B2 + B2-verify Check 1).
/// Uses CachingLlmClient(replay) against the committed fixtures/llm-cache.json.
/// Fully offline once the cache file is committed — no OPENAI_API_KEY needed at test time.
///
/// L1  Free-text signal via committed cache → correct belief (Operational/uptime_sla, Reported, Llm)
/// L2  Replay cache miss → LlmCacheMissException propagates (no network fallback)
/// L3  Two identical runs → identical belief (determinism with DemoClock.Fixed)
/// L4  Cloudwave + Signal 11 → band/stance (pinned after first green run)
/// L5  Fingerprint pin with LLM belief (pinned after first green run)
/// </summary>
public sealed class LlmStreamTests
{
    private static readonly Guid CwId = Guid.Parse("eeeeeeee-0001-0000-0000-000000000001");

    // Signal 11: Cloudwave CSM free-text note — same body as fixtures/signals.json
    private static Signal MakeSignal11() => new Signal(
        Id:           Guid.NewGuid(),
        EntityId:     CwId,
        CustomerId:   Guid.Parse("cccccccc-0000-0000-0000-000000000001"),
        SourceSystem: SourceSystem.HumanReport,
        ExternalId:   "hr-cw-2026-0603-csm-001",
        Payload:      new Dictionary<string, object?>
        {
            ["body"] = "Spoke with Cloudwave's account manager today. Adoption is tracking at 30-35% of licensed seats. They mentioned an SLA breach last week that is still being investigated."
        },
        ObservedAt:   new DateTimeOffset(2026, 6, 3, 11, 0, 0, TimeSpan.Zero),
        ReceivedAt:   new DateTimeOffset(2026, 6, 3, 11, 5, 0, TimeSpan.Zero),
        TraceId:      Guid.NewGuid());

    // Replay client over the committed cache — throws LlmCacheMissException on any miss
    private static CachingLlmClient MakeReplayClient() =>
        new CachingLlmClient(FindCachePath(), recordMode: false);

    // ── L1: free-text → correct belief (from committed cache) ─────────────────

    [Fact]
    [Trait("Class", "L")]
    public void L1_FreeText_Signal_ProducesCorrect_LlmBelief()
    {
        using var h = TestHarness.FreshEngineWithSeed(llm: MakeReplayClient());
        h.Ingest(MakeSignal11());

        var beliefs   = h.GetBeliefs("cloudwave");
        var llmBelief = beliefs.FirstOrDefault(b => b.ClassificationMethod == ClassificationMethod.Llm);

        // Real LLM classified the CSM note as Operational/uptime_sla = 0.50
        // (SLA breach mention → Operational dimension, confidence 0.7 from model)
        Assert.NotNull(llmBelief);
        Assert.Equal(Dimension.Operational,      llmBelief.Dimension);
        Assert.Equal("uptime_sla",               llmBelief.Criterion);
        Assert.Equal(0.5,                        llmBelief.Value, precision: 5);
        Assert.Equal(SourceTier.Reported,        llmBelief.SourceTier);
        Assert.Equal(ClassificationMethod.Llm,   llmBelief.ClassificationMethod);
        Assert.Equal(0.7, llmBelief.ClassificationConfidence ?? 0, precision: 5);
        Assert.False(string.IsNullOrEmpty(llmBelief.ReasoningSummary));
    }

    // ── L2: cache miss → LlmCacheMissException ────────────────────────────────

    [Fact]
    [Trait("Class", "L")]
    public void L2_EmptyReplayCache_Throws_LlmCacheMissException()
    {
        var tmp = Path.GetTempFileName();
        File.WriteAllText(tmp, "{}");
        try
        {
            var replayCachingLlm = new CachingLlmClient(tmp, recordMode: false);
            using var h = TestHarness.FreshEngineWithSeed(llm: replayCachingLlm);
            Assert.Throws<LlmCacheMissException>(() => h.Ingest(MakeSignal11()));
        }
        finally { File.Delete(tmp); }
    }

    // ── L3: determinism across two runs ───────────────────────────────────────

    [Fact]
    [Trait("Class", "L")]
    public void L3_TwoRuns_SameFreeTextSignal_ProduceIdenticalBeliefs()
    {
        Belief? b1, b2;

        using (var h = TestHarness.FreshEngineWithSeed(clock: DemoClock.Fixed, llm: MakeReplayClient()))
        {
            h.Ingest(MakeSignal11());
            b1 = h.GetBeliefs("cloudwave").FirstOrDefault(b => b.Criterion == "uptime_sla"
                                                             && b.ClassificationMethod == ClassificationMethod.Llm);
        }
        using (var h = TestHarness.FreshEngineWithSeed(clock: DemoClock.Fixed, llm: MakeReplayClient()))
        {
            h.Ingest(MakeSignal11());
            b2 = h.GetBeliefs("cloudwave").FirstOrDefault(b => b.Criterion == "uptime_sla"
                                                             && b.ClassificationMethod == ClassificationMethod.Llm);
        }

        Assert.NotNull(b1);
        Assert.NotNull(b2);
        Assert.Equal(b1!.Value,     b2!.Value);
        Assert.Equal(b1.Dimension,  b2.Dimension);
        Assert.Equal(b1.Criterion,  b2.Criterion);
        Assert.Equal(b1.SourceTier, b2.SourceTier);
        Assert.Equal(b1.Confidence, b2.Confidence, precision: 10);
    }

    // ── L4: band/stance ───────────────────────────────────────────────────────

    [Fact]
    [Trait("Class", "L")]
    public void L4_Cloudwave_WithLlmBelief_BandAndStance()
    {
        // Signal 11 supersedes the Operational/uptime_sla belief from Signal 1.
        // Real LLM: uptime_sla = 0.50 (Reported tier). Pin verified after first green run.
        const Band   ExpectedBand   = Band.AtRisk;
        const Stance ExpectedStance = Stance.Renegotiate;

        var fake = MakeReplayClient();
        using var h = TestHarness.FreshEngineWithSeed(clock: DemoClock.Fixed, llm: fake);
        h.ReplayAllSignals("cloudwave");
        h.Ingest(MakeSignal11());

        Assert.Equal(ExpectedBand,   h.GetIndex("cloudwave")!.Band);
        Assert.Equal(ExpectedStance, h.GetPosture("cloudwave")!.Stance);
    }

    // ── L5: fingerprint pin ───────────────────────────────────────────────────

    [Fact]
    [Trait("Class", "L")]
    [Trait("Golden", "true")]
    public void L5_Cloudwave_WithLlmBelief_FingerprintPin()
    {
        const string Pin = "7ff006ee16eeb40a2d2dfe3bc5f265e5653867d654ba7e9c2105f60bd6660464";

        using var h = TestHarness.FreshEngineWithSeed(clock: DemoClock.Fixed, llm: MakeReplayClient());
        h.ReplayAllSignals("cloudwave");
        h.Ingest(MakeSignal11());

        var actual = h.GetIndex("cloudwave")!.Fingerprint;
        Assert.Equal(64, actual.Length);
        Assert.True(actual == Pin, $"FINGERPRINT={actual}");
    }

    // ── Path helper ───────────────────────────────────────────────────────────

    private static string FindCachePath()
    {
        var dir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(dir))
        {
            var candidate = Path.Combine(dir, "fixtures", "llm-cache.json");
            if (File.Exists(candidate)) return candidate;
            dir = Path.GetDirectoryName(dir);
        }
        throw new FileNotFoundException(
            "fixtures/llm-cache.json not found. Run 'dotnet run --project tools/Kozmo.SeedPrep' first.");
    }
}
