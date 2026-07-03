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

    // ── Line-ending normalization ──────────────────────────────────────────
    //
    // Prompt text commonly originates from C# raw string literals (whose physical bytes
    // depend on .gitattributes eol rules / core.autocrlf at checkout) or from
    // JsonSerializerOptions.WriteIndented (which emits Environment.NewLine — platform
    // dependent). The same logical prompt must hash identically regardless of which line-ending
    // style it happens to carry, or cassette replay breaks silently across machines/checkouts.

    [Fact]
    public void ComputeKey_is_identical_for_CRLF_and_LF_variants_of_the_same_text()
    {
        var lf   = "line one\nline two\nline three";
        var crlf = "line one\r\nline two\r\nline three";

        var kLf   = CachingLlmClient.ComputeKey(lf,   "user", 500, "gpt-4o-mini", 0f);
        var kCrlf = CachingLlmClient.ComputeKey(crlf, "user", 500, "gpt-4o-mini", 0f);

        Assert.Equal(kLf, kCrlf);
    }

    [Fact]
    public void ComputeKey_is_identical_when_only_the_user_prompt_line_endings_differ()
    {
        var lf   = "question\n\nevidence line 1\nevidence line 2";
        var crlf = "question\r\n\r\nevidence line 1\r\nevidence line 2";

        var kLf   = CachingLlmClient.ComputeKey("system", lf,   500, "gpt-4o-mini", 0f);
        var kCrlf = CachingLlmClient.ComputeKey("system", crlf, 500, "gpt-4o-mini", 0f);

        Assert.Equal(kLf, kCrlf);
    }

    [Fact]
    public void ComputeKey_normalizes_stray_CR_as_well_as_CRLF()
    {
        // Old-Mac-style bare \r (no paired \n) must also normalize to the same key as \n.
        var lf       = "line one\nline two";
        var bareCr   = "line one\rline two";

        var kLf     = CachingLlmClient.ComputeKey(lf,     "user", 500, "gpt-4o-mini", 0f);
        var kBareCr = CachingLlmClient.ComputeKey(bareCr, "user", 500, "gpt-4o-mini", 0f);

        Assert.Equal(kLf, kBareCr);
    }

    [Fact]
    public void ComputeVisionKey_is_identical_for_CRLF_and_LF_system_prompt_variants()
    {
        var img  = new byte[] { 1, 2, 3 };
        var lf   = "instructions\nline two";
        var crlf = "instructions\r\nline two";

        var kLf   = CachingLlmClient.ComputeVisionKey(lf,   img, 2000, "gpt-4o-mini", 0f);
        var kCrlf = CachingLlmClient.ComputeVisionKey(crlf, img, 2000, "gpt-4o-mini", 0f);

        Assert.Equal(kLf, kCrlf);
    }

    // ── Vision key stability ──────────────────────────────────────────────

    [Fact]
    public void ComputeVisionKey_is_stable_for_same_inputs()
    {
        var img = new byte[] { 1, 2, 3, 4, 5 };
        var k1  = CachingLlmClient.ComputeVisionKey("system", img, 2000, "gpt-4o-mini", 0f);
        var k2  = CachingLlmClient.ComputeVisionKey("system", img, 2000, "gpt-4o-mini", 0f);
        Assert.Equal(k1, k2);
    }

    [Fact]
    public void ComputeVisionKey_differs_for_different_images()
    {
        var imgA = new byte[] { 1, 2, 3 };
        var imgB = new byte[] { 4, 5, 6 };
        var k1   = CachingLlmClient.ComputeVisionKey("system", imgA, 2000, "gpt-4o-mini", 0f);
        var k2   = CachingLlmClient.ComputeVisionKey("system", imgB, 2000, "gpt-4o-mini", 0f);
        Assert.NotEqual(k1, k2);
    }

    [Fact]
    public void ComputeVisionKey_differs_from_text_key_for_same_content()
    {
        // Vision keys include the "vision|" prefix — they must not collide with text keys.
        var img       = new byte[] { 1, 2, 3 };
        var visionKey = CachingLlmClient.ComputeVisionKey("system", img, 500, "gpt-4o-mini", 0f);
        var textKey   = CachingLlmClient.ComputeKey("system", "user", 500, "gpt-4o-mini", 0f);
        Assert.NotEqual(visionKey, textKey);
    }

    // ── Vision record/replay round-trip ───────────────────────────────────

    [Fact]
    public async Task Vision_record_then_replay_returns_identical_result()
    {
        var tmp = Path.GetTempFileName();
        try
        {
            var expected = new LlmResult(
                JsonSerializer.Deserialize<JsonElement>("\"extracted page text\""),
                Confidence: 0.75,
                ReasoningSummary: "ocr");

            var image = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }; // fake PNG header bytes
            var fake  = new FakeLlmClient(
                result:       new LlmResult(default, 0, ""),  // text path unused
                visionResult: expected);

            var recorder = new CachingLlmClient(tmp, recordMode: true, inner: fake);
            await recorder.CompleteVisionAsync("sys", image, 2000);

            var replayer = new CachingLlmClient(tmp, recordMode: false);
            var replayed = await replayer.CompleteVisionAsync("sys", image, 2000);

            Assert.Equal(expected.Confidence,        replayed.Confidence);
            Assert.Equal(expected.ReasoningSummary,  replayed.ReasoningSummary);
            Assert.Equal(
                JsonSerializer.Serialize(expected.Answer),
                JsonSerializer.Serialize(replayed.Answer));
        }
        finally { File.Delete(tmp); }
    }

    [Fact]
    public async Task Vision_replay_miss_throws_LlmCacheMissException()
    {
        var tmp = Path.GetTempFileName();
        File.WriteAllText(tmp, "{}");
        try
        {
            var replayer = new CachingLlmClient(tmp, recordMode: false);
            await Assert.ThrowsAsync<LlmCacheMissException>(() =>
                replayer.CompleteVisionAsync("sys", new byte[] { 1, 2, 3 }, 2000));
        }
        finally { File.Delete(tmp); }
    }
}
