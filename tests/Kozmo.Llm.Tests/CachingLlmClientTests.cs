using System.Text.Json;
using Xunit;

namespace Kozmo.Llm.Tests;

public sealed class CachingLlmClientTests
{
    // ── Round-trip: record then replay ────────────────────────────────────

    [Fact]
    public async Task Record_then_replay_returns_identical_result()
    {
        var tmp = Path.GetTempFileName();
        try
        {
            var expected = new LlmResult(
                JsonSerializer.Deserialize<JsonElement>("{\"verdict\":\"ok\",\"score\":0.9}"),
                Confidence: 0.92,
                ReasoningSummary: "looks good");

            var fake = new FakeLlmClient(expected);

            // Record
            var recorder = new CachingLlmClient(tmp, recordMode: true, inner: fake);
            await recorder.CompleteJsonAsync("sys prompt", "user prompt", 200);

            // Replay — new instance, no inner client
            var replayer = new CachingLlmClient(tmp, recordMode: false);
            var replayed = await replayer.CompleteJsonAsync("sys prompt", "user prompt", 200);

            Assert.Equal(expected.Confidence, replayed.Confidence);
            Assert.Equal(expected.ReasoningSummary, replayed.ReasoningSummary);
            Assert.Equal(
                JsonSerializer.Serialize(expected.Answer),
                JsonSerializer.Serialize(replayed.Answer));
        }
        finally { File.Delete(tmp); }
    }

    [Fact]
    public async Task Different_prompts_record_independently_and_replay_correctly()
    {
        var tmp = Path.GetTempFileName();
        try
        {
            var resultA = new LlmResult(
                JsonSerializer.Deserialize<JsonElement>("{\"tag\":\"A\"}"),
                0.80, "A");
            var resultB = new LlmResult(
                JsonSerializer.Deserialize<JsonElement>("{\"tag\":\"B\"}"),
                0.70, "B");

            // Record: two different prompts each go through a FakeLlmClient
            // FakeLlmClient always returns whatever was passed to it at construction,
            // so use two separate recorders with different fakes.
            var recA = new CachingLlmClient(tmp, recordMode: true, inner: new FakeLlmClient(resultA));
            await recA.CompleteJsonAsync("sys", "prompt-A", 100);

            var recB = new CachingLlmClient(tmp, recordMode: true, inner: new FakeLlmClient(resultB));
            await recB.CompleteJsonAsync("sys", "prompt-B", 100);

            // Replay both
            var replayer = new CachingLlmClient(tmp, recordMode: false);
            var repA = await replayer.CompleteJsonAsync("sys", "prompt-A", 100);
            var repB = await replayer.CompleteJsonAsync("sys", "prompt-B", 100);

            Assert.Equal("A", repA.ReasoningSummary);
            Assert.Equal("B", repB.ReasoningSummary);
        }
        finally { File.Delete(tmp); }
    }

    // ── Throw on miss ─────────────────────────────────────────────────────

    [Fact]
    public async Task Replay_with_empty_cache_throws_LlmCacheMissException()
    {
        var tmp = Path.GetTempFileName();
        File.WriteAllText(tmp, "{}"); // empty cache
        try
        {
            var replayer = new CachingLlmClient(tmp, recordMode: false);
            var ex = await Assert.ThrowsAsync<LlmCacheMissException>(() =>
                replayer.CompleteJsonAsync("sys", "user", 100));

            Assert.False(string.IsNullOrEmpty(ex.CacheKey));
        }
        finally { File.Delete(tmp); }
    }

    [Fact]
    public async Task Replay_does_not_call_network_on_miss()
    {
        // Proof: CachingLlmClient in replay mode has no inner client reference.
        // If it called the network it would throw NullReferenceException, not LlmCacheMissException.
        // We confirm it throws the RIGHT exception.
        var tmp = Path.GetTempFileName();
        File.WriteAllText(tmp, "{}");
        try
        {
            var replayer = new CachingLlmClient(tmp, recordMode: false, inner: null);
            await Assert.ThrowsAsync<LlmCacheMissException>(() =>
                replayer.CompleteJsonAsync("sys", "user", 100));
        }
        finally { File.Delete(tmp); }
    }

    // ── Key stability ─────────────────────────────────────────────────────

    [Fact]
    public void ComputeKey_is_stable_for_same_inputs()
    {
        var k1 = CachingLlmClient.ComputeKey("system", "user", 500, "gpt-4o-mini", 0f);
        var k2 = CachingLlmClient.ComputeKey("system", "user", 500, "gpt-4o-mini", 0f);
        Assert.Equal(k1, k2);
    }

    [Fact]
    public void ComputeKey_differs_when_maxTokens_differ()
    {
        var k1 = CachingLlmClient.ComputeKey("system", "user", 500, "gpt-4o-mini", 0f);
        var k2 = CachingLlmClient.ComputeKey("system", "user", 100, "gpt-4o-mini", 0f);
        Assert.NotEqual(k1, k2);
    }

    [Fact]
    public void ComputeKey_differs_when_prompt_differs()
    {
        var k1 = CachingLlmClient.ComputeKey("system-A", "user", 500, "gpt-4o-mini", 0f);
        var k2 = CachingLlmClient.ComputeKey("system-B", "user", 500, "gpt-4o-mini", 0f);
        Assert.NotEqual(k1, k2);
    }

    [Fact]
    public void ComputeKey_differs_when_model_differs()
    {
        var k1 = CachingLlmClient.ComputeKey("system", "user", 500, "gpt-4o-mini", 0f);
        var k2 = CachingLlmClient.ComputeKey("system", "user", 500, "gpt-4o",      0f);
        Assert.NotEqual(k1, k2);
    }

    [Fact]
    public void ComputeKey_differs_when_temperature_differs()
    {
        var k1 = CachingLlmClient.ComputeKey("system", "user", 500, "gpt-4o-mini", 0f);
        var k2 = CachingLlmClient.ComputeKey("system", "user", 500, "gpt-4o-mini", 0.5f);
        Assert.NotEqual(k1, k2);
    }
}
