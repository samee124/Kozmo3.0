using System.Text.Json;

namespace Kozmo.Api.Tests;

/// <summary>
/// Class N — Reset reproducibility via POST /demo/reset.
/// N1: Two sequential resets produce byte-identical fingerprints for all vendors.
/// N2: A third reset still matches the golden pins.
/// </summary>
[Collection("ApiTests")]
[Trait("Class", "N")]
public class NTests
{
    private readonly HttpClient _client;

    public NTests(ApiFixture fixture) => _client = fixture.CreateClient();

    [Fact] [Trait("Class", "N")]
    public async Task N1_TwoResets_ProduceIdenticalFingerprints()
    {
        var fps1 = await ResetAndExtractFingerprints();
        var fps2 = await ResetAndExtractFingerprints();

        Assert.Equal(fps1.Cloudwave, fps2.Cloudwave);
        Assert.Equal(fps1.Corvus,    fps2.Corvus);
        Assert.Equal(fps1.Meridian,  fps2.Meridian);
    }

    [Fact] [Trait("Class", "N")]
    public async Task N2_ThirdReset_MatchesGoldenPins()
    {
        var fps = await ResetAndExtractFingerprints();

        Assert.StartsWith("d977be9b", fps.Cloudwave);
        Assert.StartsWith("d81422d2", fps.Corvus);
        Assert.StartsWith("b2e03ff0", fps.Meridian);
    }

    private async Task<(string Cloudwave, string Corvus, string Meridian)> ResetAndExtractFingerprints()
    {
        var response = await _client.PostAsync("/demo/reset", null);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);

        var vendors = doc.RootElement.GetProperty("vendors");
        string cw = "", cor = "", mer = "";

        foreach (var v in vendors.EnumerateArray())
        {
            var id  = v.GetProperty("entityId").GetString()!;
            var fp  = v.GetProperty("fingerprint").GetString()!;
            if (id.StartsWith("eeeeeeee-0001")) cw  = fp;
            else if (id.StartsWith("eeeeeeee-0002")) cor = fp;
            else if (id.StartsWith("eeeeeeee-0003")) mer = fp;
        }

        return (cw, cor, mer);
    }
}
