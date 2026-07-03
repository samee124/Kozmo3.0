using Ii.Observation;
using Kozmo.Contracts;
using Xunit;

namespace Ii.Tests;

/// <summary>
/// ApplyNumericThresholds boundary regression — a value outside a rubric criterion's domain
/// must not silently fall into whichever bucket happens to be last in the JSON array. Before
/// the fix, the last-index catch-all matched ANY out-of-range value (above the top bucket or
/// below the bottom) to the lowest score, with no error.
/// </summary>
public sealed class ObservationModuleThresholdTests
{
    private static readonly DateTimeOffset ObservedAt = new(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void OutOfRange_UptimePercentage_DoesNotSilentlyScoreAsLowestBucket()
    {
        // uptime_sla's lowest bucket (0.0-95.0) scores 0.10. A physically impossible 150%
        // uptime must not silently land there — it must abstain (null), not fabricate a score.
        var profile = TestHelpers.LoadProfile();
        var module  = new ObservationModule();
        var signal  = MakeSignal(SourceSystem.MonitoringPlatform, "uptime_pct", 150.0);

        var result = module.Classify(signal, profile);

        Assert.Null(result);
    }

    [Fact]
    public void OutOfRange_CsatOnHundredPointScale_DoesNotSilentlyScoreAsLowestBucket()
    {
        // csat_score's rubric only covers a 1.0-5.0 rating. A 0-100 scale value (e.g. "92%")
        // must not silently land in the lowest bucket (1.0-2.0 -> 0.15) just because it's last
        // in the array — it must abstain (null) rather than mis-bucket.
        var profile = TestHelpers.LoadProfile();
        var module  = new ObservationModule();
        var signal  = MakeSignal(SourceSystem.CRM, "csat_score", 92.0);

        var result = module.Classify(signal, profile);

        Assert.Null(result);
    }

    [Fact]
    public void ExactDomainCeiling_UptimePercentage_ScoresTopBucket()
    {
        // A value exactly at the domain ceiling (100% uptime) is legitimate, not out-of-range,
        // and must score the top bucket (1.00) rather than falling through to the bottom.
        var profile = TestHelpers.LoadProfile();
        var module  = new ObservationModule();
        var signal  = MakeSignal(SourceSystem.MonitoringPlatform, "uptime_pct", 100.0);

        var result = module.Classify(signal, profile);

        Assert.NotNull(result);
        Assert.Equal(1.00, result!.Value, precision: 6);
    }

    [Fact]
    public void InRange_UptimePercentage_MatchesGoldenBand()
    {
        // Regression guard: the fix must not change any in-domain classification.
        // 98.5% uptime -> 0.45, per the Cloudwave golden fixture (fixtures/signals.json).
        var profile = TestHelpers.LoadProfile();
        var module  = new ObservationModule();
        var signal  = MakeSignal(SourceSystem.MonitoringPlatform, "uptime_pct", 98.5);

        var result = module.Classify(signal, profile);

        Assert.NotNull(result);
        Assert.Equal(0.45, result!.Value, precision: 6);
    }

    [Fact]
    public void InRange_Csat_MatchesGoldenBand()
    {
        // Regression guard: 4.2/5.0 CSAT -> 0.80, per the Helix golden fixture.
        var profile = TestHelpers.LoadProfile();
        var module  = new ObservationModule();
        var signal  = MakeSignal(SourceSystem.CRM, "csat_score", 4.2);

        var result = module.Classify(signal, profile);

        Assert.NotNull(result);
        Assert.Equal(0.80, result!.Value, precision: 6);
    }

    private static Signal MakeSignal(SourceSystem sourceSystem, string payloadKey, double value) =>
        new(
            Id:         Guid.NewGuid(),
            EntityId:   Guid.NewGuid(),
            CustomerId: Guid.NewGuid(),
            SourceSystem: sourceSystem,
            ExternalId: "test-external-id",
            Payload:    new Dictionary<string, object?> { [payloadKey] = value },
            ObservedAt: ObservedAt,
            ReceivedAt: ObservedAt,
            TraceId:    Guid.NewGuid());
}
