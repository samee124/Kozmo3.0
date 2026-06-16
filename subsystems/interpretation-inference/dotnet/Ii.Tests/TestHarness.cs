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

namespace Ii.Tests;

/// <summary>
/// Test-only façade that wires a fresh in-memory I&amp;I engine.
/// Entity registry is pre-loaded; signals are submitted on demand via ReplayAllSignals / Ingest.
/// Exposes inner modules (Profile, Decay, Rubric, Index) for module-level tests (Class D, E).
/// </summary>
internal sealed class TestHarness : IDisposable
{
    // ── Vendor identity ───────────────────────────────────────────────────────

    private static readonly Guid CwId  = Guid.Parse("eeeeeeee-0001-0000-0000-000000000001");
    private static readonly Guid CorId = Guid.Parse("eeeeeeee-0002-0000-0000-000000000001");
    private static readonly Guid MerId = Guid.Parse("eeeeeeee-0003-0000-0000-000000000001");

    public static readonly IReadOnlyDictionary<string, Guid> VendorIds =
        new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase)
        {
            ["cloudwave"] = CwId,
            ["corvus"]    = CorId,
            ["meridian"]  = MerId
        };

    // ── Inner modules — exposed for module-level tests ────────────────────────

    public readonly SaasProfile  Profile;
    public readonly DecayEngine  Decay  = new();
    public readonly RubricModule Rubric = new();
    public readonly IndexModule  Index  = new();

    // ── Pipeline state ────────────────────────────────────────────────────────

    private readonly IIiFacade         _facade;
    private readonly SqliteEntityStore _store;

    private TestHarness(IIiFacade facade, SqliteEntityStore store, SaasProfile profile)
    {
        _facade = facade;
        _store  = store;
        Profile = profile;
    }

    // ── Factory ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a fresh in-memory engine with the entity registry pre-loaded.
    /// No signals are submitted — call ReplayAllSignals or Ingest to populate data.
    /// Pass DemoClock.Fixed to pin ComputedAt/AssignedAt to a canonical timestamp.
    /// Pass an <see cref="IKozmoLlm"/> to enable the LLM path in ObservationModule (e.g. for Class L tests).
    /// </summary>
    public static TestHarness FreshEngineWithSeed(IClock? clock = null, IKozmoLlm? llm = null)
    {
        var profile  = TestHelpers.LoadProfile();
        var store    = new SqliteEntityStore("Data Source=:memory:");
        var registry = new EntityRegistry();
        registry.Register(CwId,  "Cloudwave Systems Inc.",      new DateTimeOffset(2026,  9,  1, 0, 0, 0, TimeSpan.Zero));
        registry.Register(CorId, "Corvus Infrastructure Ltd.",  new DateTimeOffset(2026,  8, 15, 0, 0, 0, TimeSpan.Zero));
        registry.Register(MerId, "Meridian IT Services Ltd.",   new DateTimeOffset(2027,  1, 15, 0, 0, 0, TimeSpan.Zero));

        var facade = new IiFacade(
            new ObservationModule(llm), new RubricModule(), new IndexModule(),
            new PostureModule(), new DecayEngine(), store, profile, registry, clock);

        return new TestHarness(facade, store, profile);
    }

    // ── Signal submission ─────────────────────────────────────────────────────

    public void ReplayAllSignals(string vendor)
    {
        foreach (var sig in SignalsFor(vendor))
            _facade.SubmitSignalAsync(sig).GetAwaiter().GetResult();
    }

    public void Ingest(Signal signal) =>
        _facade.SubmitSignalAsync(signal).GetAwaiter().GetResult();

    // ── Read API ──────────────────────────────────────────────────────────────

    public EntityIndex? GetIndex(string vendor) =>
        _facade.GetIndexAsync(VendorIds[vendor]).GetAwaiter().GetResult();

    public PostureAssignment? GetPosture(string vendor) =>
        _facade.GetPostureAsync(VendorIds[vendor]).GetAwaiter().GetResult();

    public IReadOnlyList<Belief> GetBeliefs(string vendor) =>
        _store.GetCurrentBeliefsAsync(VendorIds[vendor]).GetAwaiter().GetResult();

    public ReasoningTrail? GetReasoningTrail(string vendor) =>
        _facade.GetReasoningTrailAsync(VendorIds[vendor]).GetAwaiter().GetResult();

    public void Dispose() => _store.Dispose();

    // ── Signal factories (fresh GUIDs each call — no cross-harness conflicts) ─

    /// <summary>Returns fresh signal objects for the named vendor. New GUIDs every call.</summary>
    public static IReadOnlyList<Signal> SignalsFor(string vendor) =>
        vendor.ToLowerInvariant() switch
        {
            "cloudwave" => BuildCloudwaveSignals(),
            "corvus"    => BuildCorvusSignals(),
            "meridian"  => BuildMeridianSignals(),
            _ => throw new ArgumentOutOfRangeException(nameof(vendor))
        };

    private static Signal[] BuildCloudwaveSignals() =>
    [
        Sig(CwId, SourceSystem.MonitoringPlatform, "mon-cw-s1",
            new Dictionary<string, object?> { ["uptime_pct"] = 98.5 },
            new DateTimeOffset(2026, 5, 14, 8, 0, 0, TimeSpan.Zero)),
        Sig(CwId, SourceSystem.UsageAnalytics, "ua-cw-s3",
            new Dictionary<string, object?> { ["adoption_pct"] = 35.0 },
            new DateTimeOffset(2026, 5, 18, 10, 0, 0, TimeSpan.Zero)),
        Sig(CwId, SourceSystem.BillingSystem, "bill-cw-s8",
            new Dictionary<string, object?> { ["days_overdue"] = 10.0 },
            new DateTimeOffset(2026, 5, 28, 9, 0, 0, TimeSpan.Zero)),
        Sig(CwId, SourceSystem.Email, "email-cw-s6",
            new Dictionary<string, object?> { ["renewal_intent"] = "neutral", ["entity_name"] = "Cloudwave" },
            new DateTimeOffset(2026, 5, 24, 14, 30, 0, TimeSpan.Zero))
    ];

    private static Signal[] BuildCorvusSignals() =>
    [
        Sig(CorId, SourceSystem.MonitoringPlatform, "mon-cor-s5",
            new Dictionary<string, object?> { ["uptime_pct"] = 96.5 },
            new DateTimeOffset(2026, 5, 22, 8, 0, 0, TimeSpan.Zero)),
        Sig(CorId, SourceSystem.CRM, "crm-cor-s7",
            new Dictionary<string, object?> { ["csat_score"] = 2.3 },
            new DateTimeOffset(2026, 5, 26, 11, 0, 0, TimeSpan.Zero)),
        Sig(CorId, SourceSystem.BillingSystem, "bill-cor-s9",
            new Dictionary<string, object?> { ["overdue_amount_usd"] = 52000.0 },
            new DateTimeOffset(2026, 5, 30, 10, 0, 0, TimeSpan.Zero)),
        Sig(CorId, SourceSystem.CRM, "crm-cor-s10",
            new Dictionary<string, object?> { ["roadmap_fit_score"] = 0.30 },
            new DateTimeOffset(2026, 6, 1, 9, 0, 0, TimeSpan.Zero))
    ];

    private static Signal[] BuildMeridianSignals() =>
    [
        Sig(MerId, SourceSystem.MonitoringPlatform, "mon-mer-s2",
            new Dictionary<string, object?> { ["uptime_pct"] = 99.2 },
            new DateTimeOffset(2026, 5, 16, 8, 0, 0, TimeSpan.Zero)),
        Sig(MerId, SourceSystem.BillingSystem, "bill-mer-s4",
            new Dictionary<string, object?> { ["days_overdue"] = 0.0 },
            new DateTimeOffset(2026, 5, 20, 9, 0, 0, TimeSpan.Zero))
    ];

    private static Signal Sig(
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
