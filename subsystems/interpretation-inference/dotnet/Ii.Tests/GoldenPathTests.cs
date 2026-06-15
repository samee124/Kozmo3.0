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
/// Scenario tests against the three demo vendors + 10 frozen signals.
/// Verifies golden dimension scores, bands, and stances match the expected values
/// defined in fixtures/vendors.json and PHASE_0.md §6.
/// </summary>
public sealed class GoldenPathTests : IDisposable
{
    private readonly IIiFacade    _facade;
    private readonly SqliteEntityStore _store;
    private readonly EntityRegistry    _registry;
    private static readonly DateTimeOffset AssessAt = new(2026, 6, 12, 10, 0, 0, TimeSpan.Zero);

    private static readonly Guid CwId  = Guid.Parse("eeeeeeee-0001-0000-0000-000000000001");
    private static readonly Guid CorId = Guid.Parse("eeeeeeee-0002-0000-0000-000000000001");
    private static readonly Guid MerId = Guid.Parse("eeeeeeee-0003-0000-0000-000000000001");

    public GoldenPathTests()
    {
        var profile  = TestHelpers.LoadProfile();
        _store       = new SqliteEntityStore("Data Source=:memory:");
        _registry    = new EntityRegistry();

        _registry.Register(CwId,  "Cloudwave Systems Inc.",  new DateTimeOffset(2026, 9,  1, 0, 0, 0, TimeSpan.Zero));
        _registry.Register(CorId, "Corvus Infrastructure Ltd.", new DateTimeOffset(2026, 8, 15, 0, 0, 0, TimeSpan.Zero));
        _registry.Register(MerId, "Meridian IT Services Ltd.",  new DateTimeOffset(2027, 1, 15, 0, 0, 0, TimeSpan.Zero));

        _facade = new IiFacade(
            new ObservationModule(), new RubricModule(), new IndexModule(),
            new PostureModule(),     new DecayEngine(),  _store,
            profile, _registry);
    }

    // ── Cloudwave ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Cloudwave_DimensionScores_MatchGolden()
    {
        await SubmitCloudwaveSignals();

        var beliefs = await _facade.GetBeliefsAsync(CwId);
        Assert.Equal(4, beliefs.Count);

        var byDim = beliefs.ToDictionary(b => b.Dimension);
        Assert.Equal(0.45, byDim[Dimension.Operational].Value,  precision: 6);
        Assert.Equal(0.40, byDim[Dimension.Experiential].Value, precision: 6);
        Assert.Equal(0.55, byDim[Dimension.Financial].Value,    precision: 6);
        Assert.Equal(0.50, byDim[Dimension.Strategic].Value,    precision: 6);
    }

    [Fact]
    public async Task Cloudwave_Band_IsAtRisk()
    {
        await SubmitCloudwaveSignals();
        var idx = await _facade.GetIndexAsync(CwId);
        Assert.NotNull(idx);
        Assert.Equal(Band.AtRisk, idx.Band);
    }

    [Fact]
    public async Task Cloudwave_Stance_IsRenegotiate()
    {
        await SubmitCloudwaveSignals();
        var posture = await _facade.GetPostureAsync(CwId);
        Assert.NotNull(posture);
        Assert.Equal(Stance.Renegotiate, posture.Stance);
    }

    [Fact]
    public async Task Cloudwave_Fingerprint_IsStable()
    {
        await SubmitCloudwaveSignals();
        var idx1 = await _facade.GetIndexAsync(CwId);

        // Reset and replay — must get byte-identical fingerprint
        await _facade.ResetAsync();
        _registry.Register(CwId, "Cloudwave Systems Inc.", new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero));
        await SubmitCloudwaveSignals();
        var idx2 = await _facade.GetIndexAsync(CwId);

        Assert.Equal(idx1!.Fingerprint, idx2!.Fingerprint);
    }

    // ── Corvus ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Corvus_Band_IsCritical()
    {
        await SubmitCorvusSignals();
        var idx = await _facade.GetIndexAsync(CorId);
        Assert.NotNull(idx);
        Assert.Equal(Band.Critical, idx.Band);
    }

    [Fact]
    public async Task Corvus_Stance_IsEscalate()
    {
        await SubmitCorvusSignals();
        var posture = await _facade.GetPostureAsync(CorId);
        Assert.NotNull(posture);
        Assert.Equal(Stance.Escalate, posture.Stance);
    }

    [Fact]
    public async Task Corvus_ConfidenceFloor_IsAbove060()
    {
        await SubmitCorvusSignals();
        var idx = await _facade.GetIndexAsync(CorId);
        Assert.NotNull(idx);
        Assert.True(idx.ConfidenceFloor >= 0.60,
            $"ConfidenceFloor {idx.ConfidenceFloor} should be >= 0.60 for Corvus (all Verified signals)");
    }

    // ── Meridian ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Meridian_Band_IsHealthy()
    {
        await SubmitMeridianSignals();
        var idx = await _facade.GetIndexAsync(MerId);
        Assert.NotNull(idx);
        Assert.Equal(Band.Healthy, idx.Band);
    }

    [Fact]
    public async Task Meridian_Stance_IsMaintain()
    {
        await SubmitMeridianSignals();
        var posture = await _facade.GetPostureAsync(MerId);
        Assert.NotNull(posture);
        Assert.Equal(Stance.Maintain, posture.Stance);
    }

    // ── Tracer bullet (step 0.7): one signal → stance end-to-end ─────────────

    [Fact]
    public async Task TracerBullet_OneSignal_ProducesStance()
    {
        var signal = MakeSignal(CwId, SourceSystem.MonitoringPlatform,
                                "mon-tracer-001",
                                new Dictionary<string, object?> { ["uptime_pct"] = 98.5 },
                                new DateTimeOffset(2026, 6, 10, 8, 0, 0, TimeSpan.Zero));

        var traceId = await _facade.SubmitSignalAsync(signal);
        Assert.NotEqual(Guid.Empty, traceId);

        var posture = await _facade.GetPostureAsync(CwId);
        Assert.NotNull(posture);
        Assert.IsType<Stance>(posture.Stance);

        var trail = await _facade.GetReasoningTrailAsync(CwId);
        Assert.NotNull(trail);
        Assert.NotNull(trail.Index);
        Assert.NotNull(trail.Posture);
        Assert.NotEmpty(trail.CurrentBeliefs);
        Assert.NotEmpty(trail.SourceSignals);
    }

    // ── Signal alias resolution ───────────────────────────────────────────────

    [Fact]
    public async Task Signal6_AliasCloudwave_ClassifiesUnderCanonicalEntity()
    {
        // Signal #6 has entity_name "Cloudwave" in payload — alias for "Cloudwave Systems Inc."
        var signal = new Signal(
            Id:           Guid.Parse("11111111-0000-0000-0000-000000000006"),
            EntityId:     CwId,
            CustomerId:   Guid.Parse("cccccccc-0000-0000-0000-000000000001"),
            SourceSystem: SourceSystem.Email,
            ExternalId:   "email-alias-test",
            Payload:      new Dictionary<string, object?>
            {
                ["renewal_intent"] = "neutral",
                ["entity_name"]    = "Cloudwave"
            },
            ObservedAt:  new DateTimeOffset(2026, 5, 24, 14, 30, 0, TimeSpan.Zero),
            ReceivedAt:  new DateTimeOffset(2026, 5, 24, 14, 35, 0, TimeSpan.Zero),
            TraceId:     Guid.Parse("a6a6a6a6-0000-0000-0000-000000000001"));

        await _facade.SubmitSignalAsync(signal);

        var beliefs = await _facade.GetBeliefsAsync(CwId);
        Assert.Contains(beliefs, b => b.Dimension == Dimension.Strategic
                                   && b.Criterion  == "renewal_intent"
                                   && Math.Abs(b.Value - 0.50) < 0.001);
    }

    public void Dispose() => _store.Dispose();

    // ── Signal helpers ────────────────────────────────────────────────────────

    private Task SubmitCloudwaveSignals() => Task.WhenAll(
        // Signal 1: Operational
        _facade.SubmitSignalAsync(MakeSignal(CwId, SourceSystem.MonitoringPlatform, "mon-cw-s1",
            new Dictionary<string, object?> { ["uptime_pct"] = 98.5 },
            new DateTimeOffset(2026, 5, 14, 8, 0, 0, TimeSpan.Zero))),
        // Signal 3: Experiential
        _facade.SubmitSignalAsync(MakeSignal(CwId, SourceSystem.UsageAnalytics, "ua-cw-s3",
            new Dictionary<string, object?> { ["adoption_pct"] = 35.0 },
            new DateTimeOffset(2026, 5, 18, 10, 0, 0, TimeSpan.Zero))),
        // Signal 8: Financial
        _facade.SubmitSignalAsync(MakeSignal(CwId, SourceSystem.BillingSystem, "bill-cw-s8",
            new Dictionary<string, object?> { ["days_overdue"] = 10.0 },
            new DateTimeOffset(2026, 5, 28, 9, 0, 0, TimeSpan.Zero))),
        // Signal 6: Strategic (alias)
        _facade.SubmitSignalAsync(MakeSignal(CwId, SourceSystem.Email, "email-cw-s6",
            new Dictionary<string, object?> { ["renewal_intent"] = "neutral", ["entity_name"] = "Cloudwave" },
            new DateTimeOffset(2026, 5, 24, 14, 30, 0, TimeSpan.Zero))));

    private Task SubmitCorvusSignals() => Task.WhenAll(
        // Signal 5: Operational
        _facade.SubmitSignalAsync(MakeSignal(CorId, SourceSystem.MonitoringPlatform, "mon-cor-s5",
            new Dictionary<string, object?> { ["uptime_pct"] = 96.5 },
            new DateTimeOffset(2026, 5, 22, 8, 0, 0, TimeSpan.Zero))),
        // Signal 7: Experiential
        _facade.SubmitSignalAsync(MakeSignal(CorId, SourceSystem.CRM, "crm-cor-s7",
            new Dictionary<string, object?> { ["csat_score"] = 2.3 },
            new DateTimeOffset(2026, 5, 26, 11, 0, 0, TimeSpan.Zero))),
        // Signal 9: Financial
        _facade.SubmitSignalAsync(MakeSignal(CorId, SourceSystem.BillingSystem, "bill-cor-s9",
            new Dictionary<string, object?> { ["overdue_amount_usd"] = 52000.0 },
            new DateTimeOffset(2026, 5, 30, 10, 0, 0, TimeSpan.Zero))),
        // Signal 10: Strategic
        _facade.SubmitSignalAsync(MakeSignal(CorId, SourceSystem.CRM, "crm-cor-s10",
            new Dictionary<string, object?> { ["roadmap_fit_score"] = 0.30 },
            new DateTimeOffset(2026, 6, 1, 9, 0, 0, TimeSpan.Zero))));

    private Task SubmitMeridianSignals() => Task.WhenAll(
        // Signal 2: Operational
        _facade.SubmitSignalAsync(MakeSignal(MerId, SourceSystem.MonitoringPlatform, "mon-mer-s2",
            new Dictionary<string, object?> { ["uptime_pct"] = 99.2 },
            new DateTimeOffset(2026, 5, 16, 8, 0, 0, TimeSpan.Zero))),
        // Signal 4: Financial
        _facade.SubmitSignalAsync(MakeSignal(MerId, SourceSystem.BillingSystem, "bill-mer-s4",
            new Dictionary<string, object?> { ["days_overdue"] = 0.0 },
            new DateTimeOffset(2026, 5, 20, 9, 0, 0, TimeSpan.Zero))));

    private static Signal MakeSignal(
        Guid entityId, SourceSystem source, string externalId,
        IReadOnlyDictionary<string, object?> payload, DateTimeOffset observedAt) =>
        new Signal(
            Id:           Guid.NewGuid(),
            EntityId:     entityId,
            CustomerId:   Guid.Parse("cccccccc-0000-0000-0000-000000000001"),
            SourceSystem: source,
            ExternalId:   externalId,
            Payload:      payload,
            ObservedAt:   observedAt,
            ReceivedAt:   observedAt.AddSeconds(30),
            TraceId:      Guid.NewGuid());
}
