using System.Net.Http.Json;
using System.Text.Json;

namespace Kozmo.Api.Tests;

/// <summary>
/// Class L — Drill-down trail assertions against GET /vendors/{id}/trail.
/// L1: Corvus band.drivenBy == "composite" — all Corvus dims uniformly Critical; composite drives it, no floor override.
/// L2: Cloudwave posture.renewal.windowActive == true.
/// L3: Every belief in the trail has a non-null source signal (chain is complete).
///</summary>
[Collection("ApiTests")]
[Trait("Class", "L")]
public class LTests
{
    private readonly HttpClient _client;
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private const string CloudwaveId = "eeeeeeee-0001-0000-0000-000000000001";
    private const string CorvusId    = "eeeeeeee-0002-0000-0000-000000000001";
    private const string MeridianId  = "eeeeeeee-0003-0000-0000-000000000001";

    public LTests(ApiFixture fixture) => _client = fixture.CreateClient();

    [Fact] [Trait("Class", "L")]
    public async Task L1_CorvusTrail_BandDrivenBy_IsComposite()
    {
        // All Corvus dimension scores (0.20–0.35) are in the Critical range (< 0.40).
        // compositeBand = Critical, worstBand = Critical — neither elevates the other.
        // BandDrivenBy = "composite": the composite itself landed in Critical; no floor override.
        var trail = await _client.GetFromJsonAsync<ReasoningTrailDto>($"/vendors/{CorvusId}/trail", JsonOpts);

        Assert.NotNull(trail);
        Assert.Equal("composite", trail!.Band.DrivenBy);
    }

    [Fact] [Trait("Class", "L")]
    public async Task L2_CloudwaveTrail_RenewalWindow_IsActive()
    {
        var trail = await _client.GetFromJsonAsync<ReasoningTrailDto>($"/vendors/{CloudwaveId}/trail", JsonOpts);

        Assert.NotNull(trail);
        Assert.NotNull(trail!.Posture.Renewal);
        Assert.True(trail.Posture.Renewal!.WindowActive,
            $"Expected windowActive=true, got DaysToRenewal={trail.Posture.Renewal.DaysToRenewal}");
    }

    [Fact] [Trait("Class", "L")]
    public async Task L3_AllVendors_TrailBeliefs_HaveSourceSignal()
    {
        foreach (var id in new[] { CloudwaveId, CorvusId, MeridianId })
        {
            var trail = await _client.GetFromJsonAsync<ReasoningTrailDto>($"/vendors/{id}/trail", JsonOpts);
            Assert.NotNull(trail);

            foreach (var dim in trail!.Dimensions)
            foreach (var belief in dim.Beliefs)
                Assert.True(belief.Signal != null,
                    $"Vendor {id}, dim {dim.Dimension}, criterion {belief.Criterion}: Signal is null");
        }
    }
}
