using Ii.Contracts;
using Ii.Decay;
using Ii.Index;
using Ii.Observation;
using Ii.Posture;
using Ii.Rubric;
using Ii.Spine;
using Km.Store;
using Kozmo.Contracts;
using Xunit;

namespace Ii.Tests;

/// <summary>
/// Class S — Store supersession and idempotency (B2-verify Check 2).
/// Tests run first; red/green is reported before any fix.
///
/// S1  Two different signals, same (entity, dimension, criterion) slot
///     → predecessor is in history (count=2), newer is current
/// S2  Same signal re-delivered (identical Id+content)
///     → no duplicate; idempotent
/// </summary>
public sealed class StoreSupersessionTests : IDisposable
{
    private readonly SqliteEntityStore _store = new("Data Source=:memory:");
    private readonly Guid _entityId = Guid.NewGuid();

    public void Dispose() => _store.Dispose();

    // ── S1: Supersession preserves history ────────────────────────────────────

    [Fact]
    [Trait("Class", "S")]
    public async Task S1_TwoSignals_SameSlot_PredecessorInHistory_NewerIsCurrent()
    {
        // Belief A: first version of adoption_rate
        var beliefA = MakeBelief("adoption_rate", value: 0.40, version: 1);
        // Belief B: superseding version
        var beliefB = MakeBelief("adoption_rate", value: 0.50, version: 2);

        // Replicate IiFacade's supersession order: insert B first, then re-insert A with SupersededBy set
        await _store.AppendBeliefAsync(beliefB);
        await _store.AppendBeliefAsync(beliefA with { SupersededBy = beliefB.Id });

        var history = await _store.GetBeliefHistoryAsync(_entityId);
        var current = await _store.GetCurrentBeliefsAsync(_entityId);

        // History must contain both rows (predecessor is preserved, not deleted)
        Assert.Equal(2, history.Count);

        // Current must contain only B (the unsuperseded belief)
        Assert.Single(current);
        Assert.Equal(beliefB.Id, current[0].Id);

        // A must be marked as superseded in history
        var predecessor = history.First(b => b.Id == beliefA.Id);
        Assert.Equal(beliefB.Id, predecessor.SupersededBy);
    }

    // ── S2: Idempotent signal re-delivery ─────────────────────────────────────

    [Fact]
    [Trait("Class", "S")]
    public async Task S2_SameSignal_ReDelivered_IsIdempotent()
    {
        // A signal with a fixed Id (simulating a re-delivered message)
        var signal = new Signal(
            Id:           Guid.NewGuid(),
            EntityId:     _entityId,
            CustomerId:   Guid.NewGuid(),
            SourceSystem: SourceSystem.HumanReport,
            ExternalId:   "test-redelivery-001",
            Payload:      new Dictionary<string, object?>(),
            ObservedAt:   new DateTimeOffset(2026, 6, 3, 11, 0, 0, TimeSpan.Zero),
            ReceivedAt:   new DateTimeOffset(2026, 6, 3, 11, 5, 0, TimeSpan.Zero),
            TraceId:      Guid.NewGuid());

        await _store.AppendSignalAsync(signal);
        await _store.AppendSignalAsync(signal); // idempotent re-delivery

        var signals = await _store.GetSignalsForEntityAsync(_entityId);
        Assert.Single(signals); // no duplicate row
    }

    // ── S3: End-to-end supersession via facade ────────────────────────────────

    [Fact]
    [Trait("Class", "S")]
    public async Task S3_FacadeSupersession_EndToEnd_HistoryPreserved()
    {
        // Wire a full engine to verify the complete IiFacade supersession path
        var profile  = TestHelpers.LoadProfile();
        using var store = new SqliteEntityStore("Data Source=:memory:");
        var registry = new EntityRegistry();
        var entityId = Guid.Parse("eeeeeeee-0001-0000-0000-000000000001");

        registry.Register(entityId, "Cloudwave Systems Inc.",
            new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero));

        var facade = new IiFacade(
            new ObservationModule(), new RubricModule(), new IndexModule(),
            new PostureModule(), new DecayEngine(), store, profile, registry, DemoClock.Fixed);

        // Signal A: adoption_pct = 35 → Experiential/adoption_rate
        await facade.SubmitSignalAsync(new Signal(
            Id: Guid.NewGuid(), EntityId: entityId,
            CustomerId: Guid.Parse("cccccccc-0000-0000-0000-000000000001"),
            SourceSystem: SourceSystem.UsageAnalytics, ExternalId: "ua-s3a",
            Payload: new Dictionary<string, object?> { ["adoption_pct"] = 35.0 },
            ObservedAt:  new DateTimeOffset(2026, 5, 18, 10, 0, 0, TimeSpan.Zero),
            ReceivedAt:  new DateTimeOffset(2026, 5, 18, 10, 5, 0, TimeSpan.Zero),
            TraceId: Guid.NewGuid()));

        // Signal B: adoption_pct = 50 → supersedes A in the Experiential/adoption_rate slot
        await facade.SubmitSignalAsync(new Signal(
            Id: Guid.NewGuid(), EntityId: entityId,
            CustomerId: Guid.Parse("cccccccc-0000-0000-0000-000000000001"),
            SourceSystem: SourceSystem.UsageAnalytics, ExternalId: "ua-s3b",
            Payload: new Dictionary<string, object?> { ["adoption_pct"] = 50.0 },
            ObservedAt:  new DateTimeOffset(2026, 5, 25, 10, 0, 0, TimeSpan.Zero),
            ReceivedAt:  new DateTimeOffset(2026, 5, 25, 10, 5, 0, TimeSpan.Zero),
            TraceId: Guid.NewGuid()));

        var history = await store.GetBeliefHistoryAsync(entityId);
        var current = await store.GetCurrentBeliefsAsync(entityId);

        // History has 2 adoption_rate rows (version 1 superseded + version 2 current)
        var adoptionHistory = history.Where(b => b.Criterion == "adoption_rate").ToList();
        Assert.Equal(2, adoptionHistory.Count);

        // Current has only one adoption_rate belief
        var adoptionCurrent = current.Where(b => b.Criterion == "adoption_rate").ToList();
        Assert.Single(adoptionCurrent);

        // The current one has the newer value (50% → normalised value > the 35% version)
        Assert.True(adoptionCurrent[0].Value > 0.40,
            $"Current adoption_rate value {adoptionCurrent[0].Value:F4} should exceed v1 (35% → ~0.40)");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private Belief MakeBelief(string criterion, double value, int version) =>
        new Belief(
            Id:            Guid.NewGuid(),
            EntityId:      _entityId,
            Dimension:     Dimension.Experiential,
            Criterion:     criterion,
            Value:         value,
            SourceTier:    SourceTier.Verified,
            Confidence:    0.85,
            Freshness:     0.90,
            Derivation:    $"test:{criterion}",
            SourceSignals: [],
            Version:       version,
            SupersededBy:  null,
            CreatedAt:     new DateTimeOffset(2026, 5, 14, 8, 0, 0, TimeSpan.Zero),
            TraceId:       Guid.NewGuid());
}
