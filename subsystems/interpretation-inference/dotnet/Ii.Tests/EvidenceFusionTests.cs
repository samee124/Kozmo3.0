using System.Text.Json;
using Ii.Decay;
using Ii.Index;
using Ii.Observation;
using Ii.Posture;
using Ii.Rubric;
using Ii.Spine;
using Km.Store;
using Kozmo.Contracts;
using Kozmo.Llm;
using Xunit;

namespace Ii.Tests;

/// <summary>
/// Class Q — Evidence-fusion fault + invariant (B3-fix).
///
/// Q1  Full Corvus stream + Signal 12 → Band=Critical (integration, same scenario as G2 but
///     named explicitly as the fault pin). RED until confidence anchor implemented.
///
/// Q2  Abstract invariant: entity with four Verified Critical-range beliefs; adding a Reported
///     corroborating belief in the SAME slot must NOT lower the band below Critical.
///     RED until confidence anchor implemented.
/// </summary>
public sealed class EvidenceFusionTests
{
    private static readonly Guid CorId = Guid.Parse("eeeeeeee-0002-0000-0000-000000000001");

    private static CachingLlmClient MakeReplayClient() =>
        new CachingLlmClient(TestHelpers.FindLlmCachePath(), recordMode: false);

    // Signal 12 body must match fixtures/signals.json exactly for cache hit
    private static Signal MakeSignal12() => new Signal(
        Id:           Guid.NewGuid(),
        EntityId:     CorId,
        CustomerId:   Guid.Parse("cccccccc-0000-0000-0000-000000000001"),
        SourceSystem: SourceSystem.HumanReport,
        ExternalId:   "hr-cor-q1",
        Payload:      new Dictionary<string, object?>
        {
            ["body"] = "Support ticket response times from Corvus have been consistently above 48 hours over the past month. Three critical incidents were not resolved within SLA. The team is losing confidence in the platform's reliability and we are considering escalating this to executive level."
        },
        ObservedAt:   new DateTimeOffset(2026, 6, 5, 9, 0, 0, TimeSpan.Zero),
        ReceivedAt:   new DateTimeOffset(2026, 6, 5, 9, 10, 0, TimeSpan.Zero),
        TraceId:      Guid.NewGuid());

    // ── Q1: integration — full Corvus + S12 → Critical/Escalate ──────────────
    // RED until confidence anchor implemented (S12 Reported currently collapses
    // Operational confidence to ≈0.40 < 0.60 → AtRisk).

    [Fact]
    [Trait("Class", "Q")]
    public void Q1_Corvus_WithCorroboratingReportedBelief_RemainsAtCritical()
    {
        using var h = TestHarness.FreshEngineWithSeed(clock: DemoClock.Fixed, llm: MakeReplayClient());
        h.ReplayAllSignals("corvus");
        h.Ingest(MakeSignal12());

        Assert.Equal(Band.Critical,   h.GetIndex("corvus")!.Band);
        Assert.Equal(Stance.Escalate, h.GetPosture("corvus")!.Stance);
    }

    // ── Q2: abstract invariant ────────────────────────────────────────────────
    // Setup: one entity, four Verified beliefs (all Critical-range values), observed
    // 30 days before DemoClock.Fixed → Verified freshness ≈ 0.794, confidence ≈ 0.754 ≥ 0.60.
    // Band = Critical (pre-condition, must hold before the 5th signal).
    // Action: add a Reported HumanReport belief in the SAME slot as the first belief
    //   (uptime_sla), with an equally bad value (0.15), observed 2 days before clock
    //   → Reported freshness ≈ 0.955, confidence ≈ 0.477 < 0.60.
    //   With current code: confidence_floor collapses to 0.477 → AtRisk (BUG).
    //   After fix: confidence floor anchored to predecessor's 0.754 → Critical (CORRECT).
    // RED until confidence anchor implemented.

    [Fact]
    [Trait("Class", "Q")]
    public async Task Q2_CorroboratingReportedBelief_InSameSlot_MustNotDemoteBand()
    {
        var entityId   = Guid.NewGuid();
        var customerId = Guid.NewGuid();

        // DemoClock.Fixed = 2026-06-15T10:00:00Z
        // Verified signals observed 30 days before → confidence ≈ 0.754 > 0.60
        var verifiedDate = new DateTimeOffset(2026, 5, 16, 10, 0, 0, TimeSpan.Zero);
        // Reported signal observed 2 days before → confidence ≈ 0.477 < 0.60
        var reportedDate = new DateTimeOffset(2026, 6, 13, 10, 0, 0, TimeSpan.Zero);

        var profile  = TestHelpers.LoadProfile();
        using var store = new SqliteEntityStore("Data Source=:memory:");
        var registry = new EntityRegistry();
        registry.Register(entityId, "TestEntity", renewalDate: null);

        // FixedLlmClient: always returns Operational/uptime_sla=0.15 regardless of input
        var llm = new FixedLlmClient(
            "{\"dimension\":\"Operational\",\"criterion\":\"uptime_sla\",\"value\":0.15," +
            "\"confidence\":0.9,\"reasoning\":\"Fixed test response: SLA breach confirmed.\"}");

        var facade = new IiFacade(
            new ObservationModule(llm), new RubricModule(), new IndexModule(),
            new PostureModule(), new DecayEngine(), store, profile, registry, DemoClock.Fixed);

        static Signal Sig(Guid eid, Guid cid, SourceSystem src, string ext,
            IReadOnlyDictionary<string, object?> payload, DateTimeOffset obs) =>
            new Signal(Guid.NewGuid(), eid, cid, src, ext, payload,
                ObservedAt: obs, ReceivedAt: obs.AddSeconds(30), TraceId: Guid.NewGuid());

        // Four Verified Critical-range beliefs (all different slots)
        await facade.SubmitSignalAsync(Sig(entityId, customerId, SourceSystem.MonitoringPlatform, "q2-s1",
            new Dictionary<string, object?> { ["uptime_pct"] = 96.5 }, verifiedDate));
        await facade.SubmitSignalAsync(Sig(entityId, customerId, SourceSystem.CRM, "q2-s2",
            new Dictionary<string, object?> { ["csat_score"] = 2.0 }, verifiedDate));
        await facade.SubmitSignalAsync(Sig(entityId, customerId, SourceSystem.BillingSystem, "q2-s3",
            new Dictionary<string, object?> { ["overdue_amount_usd"] = 50000.0 }, verifiedDate));
        await facade.SubmitSignalAsync(Sig(entityId, customerId, SourceSystem.CRM, "q2-s4",
            new Dictionary<string, object?> { ["roadmap_fit_score"] = 0.25 }, verifiedDate));

        // Pre-condition: Band=Critical before the corroborating reported signal
        var idxBefore = await store.GetIndexAsync(entityId);
        Assert.Equal(Band.Critical, idxBefore!.Band);

        // Now add a Reported signal in the SAME slot (Operational/uptime_sla) with worse value
        await facade.SubmitSignalAsync(Sig(entityId, customerId, SourceSystem.HumanReport, "q2-s5",
            new Dictionary<string, object?> { ["body"] = "Confirmed: SLA breach, reliability very poor." },
            reportedDate));

        // Invariant: Band must still be Critical — corroborating bad news must not demote
        var idxAfter = await store.GetIndexAsync(entityId);
        Assert.Equal(Band.Critical, idxAfter!.Band);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// LLM client that always returns the same fixed JSON answer, regardless of input.
    /// For use in invariant tests that do not depend on the committed LLM cache.
    /// </summary>
    private sealed class FixedLlmClient : IKozmoLlm
    {
        private readonly JsonElement _element;

        public FixedLlmClient(string answerJson)
        {
            using var doc = JsonDocument.Parse(answerJson);
            _element = doc.RootElement.Clone();
        }

        public Task<LlmResult> CompleteJsonAsync(
            string system, string user, int maxTokens = 500, CancellationToken ct = default) =>
            Task.FromResult(new LlmResult(_element, 0.9, "Fixed test response"));
    }
}
