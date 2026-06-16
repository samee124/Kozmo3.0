using Ii.Contracts;
using Ii.Spine;
using Kozmo.Contracts;
using Kozmo.Llm;
using Xunit;

namespace Ii.Tests;

/// <summary>
/// Class G — Golden stream B3 (all three vendors with LLM beliefs).
/// Class P — Fingerprint pins for the new LLM-augmented vendor streams.
///
/// G2  Corvus  all structured signals + Signal 12 → Critical / Escalate
///     After B3-fix: confidence anchor prevents S12 (Reported) from lowering the
///     dimension confidence below S5 (Verified). Critical gate holds.
/// G3  Meridian all structured signals + Signal 13 → Healthy / Maintain
/// P1  Corvus  + Signal 12 fingerprint pin (pinned after first green run)
/// P2  Meridian + Signal 13 fingerprint pin (pinned after first green run)
/// </summary>
public sealed class GoldenStreamB3Tests
{
    private static readonly Guid CorId = Guid.Parse("eeeeeeee-0002-0000-0000-000000000001");
    private static readonly Guid MerId = Guid.Parse("eeeeeeee-0003-0000-0000-000000000001");

    private static CachingLlmClient MakeReplayClient() =>
        new CachingLlmClient(TestHelpers.FindLlmCachePath(), recordMode: false);

    // Signal 12: Corvus CSM free-text — must match fixtures/signals.json body exactly
    private static Signal MakeSignal12() => new Signal(
        Id:           Guid.NewGuid(),
        EntityId:     CorId,
        CustomerId:   Guid.Parse("cccccccc-0000-0000-0000-000000000001"),
        SourceSystem: SourceSystem.HumanReport,
        ExternalId:   "hr-cor-2026-0605-csm-001",
        Payload:      new Dictionary<string, object?>
        {
            ["body"] = "Support ticket response times from Corvus have been consistently above 48 hours over the past month. Three critical incidents were not resolved within SLA. The team is losing confidence in the platform's reliability and we are considering escalating this to executive level."
        },
        ObservedAt:   new DateTimeOffset(2026, 6, 5, 9, 0, 0, TimeSpan.Zero),
        ReceivedAt:   new DateTimeOffset(2026, 6, 5, 9, 10, 0, TimeSpan.Zero),
        TraceId:      Guid.NewGuid());

    // Signal 13: Meridian CSM free-text — must match fixtures/signals.json body exactly.
    // Reworded in B3-fix: removed explicit uptime figure that misled LLM into Operational;
    // primary signal is Strategic (renewal intent / roadmap alignment).
    private static Signal MakeSignal13() => new Signal(
        Id:           Guid.NewGuid(),
        EntityId:     MerId,
        CustomerId:   Guid.Parse("cccccccc-0000-0000-0000-000000000001"),
        SourceSystem: SourceSystem.HumanReport,
        ExternalId:   "hr-mer-2026-0607-csm-001",
        Payload:      new Dictionary<string, object?>
        {
            ["body"] = "Quarterly business review with Meridian went very well. Executive sponsor confirmed strong intent to renew and expand the license footprint next year. Product roadmap is well-aligned with our long-term strategic direction and the team is highly satisfied with responsiveness and value delivered. Recommending renewal."
        },
        ObservedAt:   new DateTimeOffset(2026, 6, 7, 10, 0, 0, TimeSpan.Zero),
        ReceivedAt:   new DateTimeOffset(2026, 6, 7, 10, 5, 0, TimeSpan.Zero),
        TraceId:      Guid.NewGuid());

    // ── G2: Corvus + Signal 12 → Critical / Escalate ─────────────────────────
    // S12 (Reported) supersedes S5 (Verified uptime_sla). The confidence anchor in
    // IiFacade ensures S12's effective confidence is floored at S5's decayed value (≥0.60).
    // Critical gate holds; more negative evidence does NOT demote the band.

    [Fact]
    [Trait("Class", "G")]
    public void G2_Corvus_WithLlmBelief_BandAndStance()
    {
        using var h = TestHarness.FreshEngineWithSeed(clock: DemoClock.Fixed, llm: MakeReplayClient());
        h.ReplayAllSignals("corvus");
        h.Ingest(MakeSignal12());

        Assert.Equal(Band.Critical,   h.GetIndex("corvus")!.Band);
        Assert.Equal(Stance.Escalate, h.GetPosture("corvus")!.Stance);
    }

    // ── G3: Meridian + Signal 13 → Healthy / Maintain ────────────────────────

    [Fact]
    [Trait("Class", "G")]
    public void G3_Meridian_WithLlmBelief_BandAndStance()
    {
        using var h = TestHarness.FreshEngineWithSeed(clock: DemoClock.Fixed, llm: MakeReplayClient());
        h.ReplayAllSignals("meridian");
        h.Ingest(MakeSignal13());

        Assert.Equal(Band.Healthy,   h.GetIndex("meridian")!.Band);
        Assert.Equal(Stance.Maintain, h.GetPosture("meridian")!.Stance);
    }

    // ── P1: Corvus + Signal 12 fingerprint pin ────────────────────────────────

    [Fact]
    [Trait("Class", "P")]
    [Trait("Golden", "true")]
    public void P1_Corvus_WithLlmBelief_FingerprintPin()
    {
        const string Pin = "9c33aaee76ce8a3df3798797d5d6194cf51765a28064f18560a38f4792b20385";

        using var h = TestHarness.FreshEngineWithSeed(clock: DemoClock.Fixed, llm: MakeReplayClient());
        h.ReplayAllSignals("corvus");
        h.Ingest(MakeSignal12());

        var actual = h.GetIndex("corvus")!.Fingerprint;
        Assert.True(actual == Pin, $"FINGERPRINT={actual}");
    }

    // ── P2: Meridian + Signal 13 fingerprint pin ──────────────────────────────

    [Fact]
    [Trait("Class", "P")]
    [Trait("Golden", "true")]
    public void P2_Meridian_WithLlmBelief_FingerprintPin()
    {
        const string Pin = "5b67c2ae7691c88cb88f7c641af974a95385d753790e8ba7b76d936314976c4a";

        using var h = TestHarness.FreshEngineWithSeed(clock: DemoClock.Fixed, llm: MakeReplayClient());
        h.ReplayAllSignals("meridian");
        h.Ingest(MakeSignal13());

        var actual = h.GetIndex("meridian")!.Fingerprint;
        Assert.True(actual == Pin, $"FINGERPRINT={actual}");
    }
}
