using System.Net.Http.Json;
using System.Text.Json;

namespace Kozmo.Api.Tests;

/// <summary>
/// Class K — Golden state assertions against GET /vendors and GET /vendors/{id}.
/// K1: list 3 summaries with correct band/stance/fingerprint prefix.
/// K2: Cloudwave detail — AtRisk/Renegotiate, 4 dimensions, renewal window active.
/// K3: Corvus detail — Critical/Escalate, confidence_floor >= 0.60.
/// K4: Meridian detail — Healthy/Maintain, composite >= 0.65.
/// </summary>
[Collection("ApiTests")]
[Trait("Class", "K")]
public class KTests
{
    private readonly HttpClient _client;
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private const string CloudwaveId = "eeeeeeee-0001-0000-0000-000000000001";
    private const string CorvusId    = "eeeeeeee-0002-0000-0000-000000000001";
    private const string MeridianId  = "eeeeeeee-0003-0000-0000-000000000001";
    private const string HelixId     = "eeeeeeee-0004-0000-0000-000000000001";

    public KTests(ApiFixture fixture) => _client = fixture.CreateClient();

    [Fact] [Trait("Class", "K")]
    public async Task K1_GetVendors_ReturnsFourGoldenSummaries()
    {
        var summaries = await _client.GetFromJsonAsync<VendorSummaryDto[]>("/vendors", JsonOpts);

        Assert.NotNull(summaries);
        Assert.True(summaries!.Length >= 4, $"Expected at least 4 vendors, got {summaries.Length}");

        var cw  = summaries.Single(v => v.EntityId == CloudwaveId);
        var cor = summaries.Single(v => v.EntityId == CorvusId);
        var mer = summaries.Single(v => v.EntityId == MeridianId);
        var hx  = summaries.Single(v => v.EntityId == HelixId);

        Assert.Equal("AtRisk",     cw.Band);
        Assert.Equal("Renegotiate", cw.Stance);
        Assert.StartsWith("d977be9b", cw.Fingerprint);

        Assert.Equal("Critical", cor.Band);
        Assert.Equal("Escalate", cor.Stance);
        Assert.StartsWith("d81422d2", cor.Fingerprint);

        Assert.Equal("Healthy",  mer.Band);
        Assert.Equal("Maintain", mer.Stance);
        Assert.StartsWith("b2e03ff0", mer.Fingerprint);

        Assert.Equal("Healthy",  hx.Band);
        Assert.Equal("Maintain", hx.Stance);
        Assert.StartsWith("987508de", hx.Fingerprint);
    }

    [Fact] [Trait("Class", "K")]
    public async Task K2_GetCloudwaveDetail_AtRiskRenegotiate_RenewalWindowActive()
    {
        var detail = await _client.GetFromJsonAsync<VendorDetailDto>($"/vendors/{CloudwaveId}", JsonOpts);

        Assert.NotNull(detail);
        Assert.Equal("AtRisk",      detail!.Index.Band);
        Assert.Equal("Renegotiate", detail.Posture.Stance);
        Assert.StartsWith("d977be9b", detail.Index.Fingerprint);

        Assert.Equal(4, detail.Index.Dimensions.Count);

        // Dimension golden values (op=0.45, exp=0.40, fin=0.55, strat=0.50)
        var op    = detail.Index.Dimensions.Single(d => d.Dimension == "Operational");
        var exp   = detail.Index.Dimensions.Single(d => d.Dimension == "Experiential");
        var fin   = detail.Index.Dimensions.Single(d => d.Dimension == "Financial");
        var strat = detail.Index.Dimensions.Single(d => d.Dimension == "Strategic");
        Assert.InRange(op.Score,    0.44, 0.46);
        Assert.InRange(exp.Score,   0.39, 0.41);
        Assert.InRange(fin.Score,   0.54, 0.56);
        Assert.InRange(strat.Score, 0.49, 0.51);

        // Renewal window: DemoClock.AsOf = Jun 15 2026, renewal = Sep 1 2026 → ~77 days → active
        Assert.NotNull(detail.Posture.Renewal);
        Assert.True(detail.Posture.Renewal!.WindowActive);
        Assert.InRange(detail.Posture.Renewal.DaysToRenewal, 70, 90);
    }

    [Fact] [Trait("Class", "K")]
    public async Task K3_GetCorvusDetail_CriticalEscalate_ConfidenceFloorAtLeast60()
    {
        var detail = await _client.GetFromJsonAsync<VendorDetailDto>($"/vendors/{CorvusId}", JsonOpts);

        Assert.NotNull(detail);
        Assert.Equal("Critical",  detail!.Index.Band);
        Assert.Equal("Escalate",  detail.Posture.Stance);
        Assert.StartsWith("d81422d2", detail.Index.Fingerprint);
        Assert.Equal(4, detail.Index.Dimensions.Count);
        Assert.True(detail.Index.ConfidenceFloor >= 0.60,
            $"Expected confidence_floor >= 0.60, got {detail.Index.ConfidenceFloor}");
    }

    [Fact] [Trait("Class", "K")]
    public async Task K4_GetMeridianDetail_HealthyMaintain()
    {
        var detail = await _client.GetFromJsonAsync<VendorDetailDto>($"/vendors/{MeridianId}", JsonOpts);

        Assert.NotNull(detail);
        Assert.Equal("Healthy",  detail!.Index.Band);
        Assert.Equal("Maintain", detail.Posture.Stance);
        Assert.StartsWith("b2e03ff0", detail.Index.Fingerprint);
        Assert.Equal(4, detail.Index.Dimensions.Count);
    }

}
