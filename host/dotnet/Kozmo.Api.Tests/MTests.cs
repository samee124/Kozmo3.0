using System.Net.Http.Json;
using System.Text.Json;

namespace Kozmo.Api.Tests;

/// <summary>
/// Class M — Trajectory assertions against GET /vendors/{id}/trajectory.
/// M1: Cloudwave — 4 points, non-decreasing timestamps, all have SignalId, final = golden.
/// M2: Corvus    — 4 points, non-decreasing timestamps, all have SignalId, final = golden.
/// M3: Meridian  — 3 points, non-decreasing timestamps, all have SignalId, final = golden.
/// </summary>
[Collection("ApiTests")]
[Trait("Class", "M")]
public class MTests
{
    private readonly HttpClient _client;
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private const string CloudwaveId = "eeeeeeee-0001-0000-0000-000000000001";
    private const string CorvusId    = "eeeeeeee-0002-0000-0000-000000000001";
    private const string MeridianId  = "eeeeeeee-0003-0000-0000-000000000001";

    public MTests(ApiFixture fixture) => _client = fixture.CreateClient();

    [Fact] [Trait("Class", "M")]
    public async Task M1_CloudwaveTrajectory_FourPoints_GoldenFinalState()
    {
        var points = await _client.GetFromJsonAsync<TrajectoryPointDto[]>(
            $"/vendors/{CloudwaveId}/trajectory", JsonOpts);

        Assert.NotNull(points);
        Assert.Equal(4, points!.Length);
        AssertNonDecreasing(points);
        Assert.All(points, p => Assert.NotNull(p.SignalId));

        var last = points.Last();
        Assert.Equal("AtRisk",      last.Band);
        Assert.Equal("Renegotiate", last.Stance);
        Assert.StartsWith("d977be9b", last.Fingerprint);
    }

    [Fact] [Trait("Class", "M")]
    public async Task M2_CorvusTrajectory_FourPoints_GoldenFinalState()
    {
        var points = await _client.GetFromJsonAsync<TrajectoryPointDto[]>(
            $"/vendors/{CorvusId}/trajectory", JsonOpts);

        Assert.NotNull(points);
        Assert.Equal(4, points!.Length);
        AssertNonDecreasing(points);
        Assert.All(points, p => Assert.NotNull(p.SignalId));

        var last = points.Last();
        Assert.Equal("Critical", last.Band);
        Assert.Equal("Escalate", last.Stance);
        Assert.StartsWith("d81422d2", last.Fingerprint);
    }

    [Fact] [Trait("Class", "M")]
    public async Task M3_MeridianTrajectory_ThreePoints_GoldenFinalState()
    {
        var points = await _client.GetFromJsonAsync<TrajectoryPointDto[]>(
            $"/vendors/{MeridianId}/trajectory", JsonOpts);

        Assert.NotNull(points);
        Assert.Equal(3, points!.Length);
        AssertNonDecreasing(points);
        Assert.All(points, p => Assert.NotNull(p.SignalId));

        var last = points.Last();
        Assert.Equal("Healthy",  last.Band);
        Assert.Equal("Maintain", last.Stance);
        Assert.StartsWith("b2e03ff0", last.Fingerprint);
    }

    private static void AssertNonDecreasing(TrajectoryPointDto[] points)
    {
        for (var i = 1; i < points.Length; i++)
            Assert.True(points[i].Timestamp >= points[i - 1].Timestamp,
                $"Trajectory not non-decreasing at index {i}: {points[i - 1].Timestamp} > {points[i].Timestamp}");
    }
}
