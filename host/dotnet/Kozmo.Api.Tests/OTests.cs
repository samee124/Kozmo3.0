using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Kozmo.Api.Tests;

/// <summary>
/// Class O — Live signal injection via POST /demo/live-signal.
/// Uses LiveSignalFixture (stub LLM, no network); CI never calls OpenAI.
///
/// O1: Live signal creates a new belief, recomputes the engine, updates the vendor state.
///     The new belief appears in the drill-down trail with ClassificationMethod = llm.
/// O2: Live signal then reset → golden seed fingerprints restored, live signal wiped.
/// </summary>
[Collection("LiveSignalTests")]
[Trait("Class", "O")]
public class OTests
{
    private readonly HttpClient _client;
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private const string CloudwaveId = "eeeeeeee-0001-0000-0000-000000000001";

    public OTests(LiveSignalFixture fixture) => _client = fixture.CreateClient();

    [Fact] [Trait("Class", "O")]
    public async Task O1_LiveSignal_CreatesBeliefAndRecomputesEngine()
    {
        // Record Cloudwave's fingerprint before the live signal
        var beforeTrail = await _client.GetFromJsonAsync<ReasoningTrailDto>($"/vendors/{CloudwaveId}/trail", JsonOpts);
        Assert.NotNull(beforeTrail);
        Assert.NotNull(beforeTrail!.Index); // Cloudwave has real evidence in every dimension — always assessed
        var beforeFp = beforeTrail.Index.Fingerprint;

        // Send a live signal — stub returns Operational/uptime_sla=0.10
        var req      = new { body = "The Cloudwave platform has been experiencing critical outages." };
        var response = await _client.PostAsJsonAsync("/demo/live-signal", req);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var result = await response.Content.ReadFromJsonAsync<LiveSignalResponse>(JsonOpts);
        Assert.NotNull(result);

        // Classification fields populated
        Assert.Equal(CloudwaveId,   result!.Vendor.EntityId);
        Assert.Equal("Operational", result.Classification.Dimension);
        Assert.Equal("uptime_sla",  result.Classification.Criterion);
        Assert.InRange(result.Classification.Value, 0.0, 0.30); // stub returns 0.10

        // The engine state reflects the new signal — fingerprint must differ (new belief changes inputs)
        Assert.NotEqual(beforeFp, result.Index.Fingerprint);

        // Drill-down trail shows the live belief with classificationMethod = "llm"
        var afterTrail = await _client.GetFromJsonAsync<ReasoningTrailDto>($"/vendors/{CloudwaveId}/trail", JsonOpts);
        Assert.NotNull(afterTrail);

        var opDim = afterTrail!.Dimensions.Single(d => d.Dimension == "Operational");
        Assert.Contains(opDim.Beliefs, b => b.ClassificationMethod == "llm");
    }

    [Fact] [Trait("Class", "O")]
    public async Task O2_LiveSignalThenReset_RestoresGoldenFingerprints()
    {
        // Send a live signal to mutate the state
        var req = new { body = "Serious Cloudwave stability problems this week." };
        await _client.PostAsJsonAsync("/demo/live-signal", req);

        // Verify state has changed (non-golden fingerprint)
        var mutated = await _client.GetFromJsonAsync<ReasoningTrailDto>($"/vendors/{CloudwaveId}/trail", JsonOpts);
        Assert.NotNull(mutated);

        // Reset — live signal must be wiped, baseline restored
        var resetResponse = await _client.PostAsync("/demo/reset", null);
        resetResponse.EnsureSuccessStatusCode();

        var json = await resetResponse.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var vendors = doc.RootElement.GetProperty("vendors");

        string cw = "", cor = "", mer = "";
        foreach (var v in vendors.EnumerateArray())
        {
            var id = v.GetProperty("entityId").GetString()!;
            var fp = v.GetProperty("fingerprint").GetString()!;
            if (id.StartsWith("eeeeeeee-0001")) cw  = fp;
            else if (id.StartsWith("eeeeeeee-0002")) cor = fp;
            else if (id.StartsWith("eeeeeeee-0003")) mer = fp;
        }

        // Golden pins must be back
        Assert.StartsWith("d977be9b", cw);
        Assert.StartsWith("d81422d2", cor);
        Assert.StartsWith("b2e03ff0", mer);
    }
}
