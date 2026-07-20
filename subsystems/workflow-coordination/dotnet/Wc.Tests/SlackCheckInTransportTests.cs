using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Wc.CheckIn;
using Wc.Contracts;
using Xunit;

namespace Wc.Tests;

using CheckIn = global::Wc.Contracts.CheckIn;

/// <summary>
/// Tests for SlackCheckInTransport and SlackSignatureVerifier.
/// HTTP calls are captured by CapturingHandler — no real Slack API is called.
/// </summary>
public sealed class SlackCheckInTransportTests
{
    private static readonly Guid   VendorId = new("AAAAAAAA-0000-0000-0000-000000000001");
    private static readonly Guid   RunId    = new("BBBBBBBB-0000-0000-0000-000000000001");
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static CheckIn Make(ResponseShape shape, string question = "Test?") => new(
        CheckInId:      Guid.NewGuid(),
        VendorId:       VendorId,
        ProgramRunId:   RunId,
        Kind:           CheckInKind.DIMENSION_GAP,
        Question:       question,
        ResponseShape:  shape,
        TargetField:    null,
        Owner:          "owner@test",
        Status:         PendingStatus.OPEN,
        RaisedAt:       Now,
        AnsweredAt:     null,
        ExpiresAt:      null,
        ResponseValue:  null);

    private static (SlackCheckInTransport transport, CapturingHandler handler) BuildTransport()
    {
        var handler   = new CapturingHandler();
        var http      = new HttpClient(handler);
        var transport = new SlackCheckInTransport(http, "xoxb-test-token", "C0123456");
        return (transport, handler);
    }

    // ── 3-question digest → exactly ONE chat.postMessage call ────────────────

    [Fact]
    public async Task ThreeQuestions_ProducesOnePostMessageCall()
    {
        var (transport, handler) = BuildTransport();

        var checkIns = new[]
        {
            Make(ResponseShape.YES_NO,        "Q1?"),
            Make(ResponseShape.TYPED_VALUE,   "Q2?"),
            Make(ResponseShape.STATUS_SELECT, "Q3?")
        };

        await transport.SendAsync(checkIns);

        Assert.Single(handler.Calls);
    }

    // ── 3-question digest → message body has 3 question blocks ───────────────

    [Fact]
    public async Task ThreeQuestions_BodyContainsThreeQuestionBlocks()
    {
        var (transport, handler) = BuildTransport();
        var q1 = Make(ResponseShape.YES_NO,      "Question one?");
        var q2 = Make(ResponseShape.TYPED_VALUE, "Question two?");
        var q3 = Make(ResponseShape.YES_NO,      "Question three?");

        await transport.SendAsync([q1, q2, q3]);

        var body = handler.Calls[0].Body;
        Assert.Contains("Question one?",   body);
        Assert.Contains("Question two?",   body);
        Assert.Contains("Question three?", body);
    }

    // ── YES_NO → 3 action buttons (Yes / No / Unsure) ────────────────────────

    [Fact]
    public async Task YesNo_RendersThreeButtons()
    {
        var (transport, handler) = BuildTransport();

        await transport.SendAsync([Make(ResponseShape.YES_NO)]);

        var body = handler.Calls[0].Body;
        var doc  = JsonDocument.Parse(body);

        // Find all "actions" blocks
        var blocks    = doc.RootElement.GetProperty("blocks");
        var actionsBlocks = blocks.EnumerateArray()
            .Where(b => b.TryGetProperty("type", out var t) && t.GetString() == "actions")
            .ToList();

        Assert.Single(actionsBlocks);
        var elements = actionsBlocks[0].GetProperty("elements");
        Assert.Equal(3, elements.GetArrayLength());

        var texts = elements.EnumerateArray()
            .Select(e => e.GetProperty("text").GetProperty("text").GetString())
            .ToList();
        Assert.Contains("Yes",    texts);
        Assert.Contains("No",     texts);
        Assert.Contains("Unsure", texts);
    }

    // ── YES_NO buttons carry {checkInId, answer} payload ─────────────────────

    [Fact]
    public async Task YesNo_ButtonValues_ContainCheckInIdAndAnswer()
    {
        var (transport, handler) = BuildTransport();
        var ci = Make(ResponseShape.YES_NO);

        await transport.SendAsync([ci]);

        var doc    = JsonDocument.Parse(handler.Calls[0].Body);
        var blocks = doc.RootElement.GetProperty("blocks");
        var elements = blocks.EnumerateArray()
            .First(b => b.TryGetProperty("type", out var t) && t.GetString() == "actions")
            .GetProperty("elements");

        foreach (var elem in elements.EnumerateArray())
        {
            var valueJson = elem.GetProperty("value").GetString()!;
            using var v   = JsonDocument.Parse(valueJson);
            Assert.Equal(ci.CheckInId.ToString(), v.RootElement.GetProperty("checkInId").GetString());
            Assert.NotEmpty(v.RootElement.GetProperty("answer").GetString()!);
        }
    }

    // ── TYPED_VALUE → link button (not three YES/NO buttons) ─────────────────

    [Fact]
    public async Task TypedValue_RendersLinkButton_NotYesNoButtons()
    {
        var (transport, handler) = BuildTransport();

        await transport.SendAsync([Make(ResponseShape.TYPED_VALUE)]);

        var doc    = JsonDocument.Parse(handler.Calls[0].Body);
        var blocks = doc.RootElement.GetProperty("blocks");
        var actionsBlock = blocks.EnumerateArray()
            .First(b => b.TryGetProperty("type", out var t) && t.GetString() == "actions");

        var elements = actionsBlock.GetProperty("elements");
        Assert.Single(elements.EnumerateArray());

        // The single button should NOT have action_id "yes_btn" / "no_btn" / "unsure_btn"
        var actionId = elements[0].GetProperty("action_id").GetString();
        Assert.Equal("open_kozmo", actionId);
    }

    // ── Transport failure → no exception escapes ──────────────────────────────

    [Fact]
    public async Task TransportFailure_DoesNotThrow()
    {
        var handler   = new ThrowingHandler();
        var http      = new HttpClient(handler);
        var transport = new SlackCheckInTransport(http, "xoxb-test", "C0123456");

        // Must not throw — transport failure must not drop the check-in
        var ex = await Record.ExceptionAsync(() => transport.SendAsync([Make(ResponseShape.YES_NO)]));
        Assert.Null(ex);
    }

    // ── Empty list → no HTTP call ─────────────────────────────────────────────

    [Fact]
    public async Task EmptyList_NoHttpCall()
    {
        var (transport, handler) = BuildTransport();

        await transport.SendAsync([]);

        Assert.Empty(handler.Calls);
    }

    // ── Posted to correct channel ─────────────────────────────────────────────

    [Fact]
    public async Task PostedToCorrectChannel()
    {
        var (transport, handler) = BuildTransport();  // destination = "C0123456"

        await transport.SendAsync([Make(ResponseShape.YES_NO)]);

        var doc     = JsonDocument.Parse(handler.Calls[0].Body);
        var channel = doc.RootElement.GetProperty("channel").GetString();
        Assert.Equal("C0123456", channel);
    }
}

// ── Slack signature verifier tests ────────────────────────────────────────────

public sealed class SlackSignatureVerifierTests
{
    private const string TestSecret = "slack-test-signing-secret-abc123";

    private static (string Timestamp, string Body, string Signature) ValidTuple(string body = "test=body")
    {
        var ts        = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var baseStr   = $"v0:{ts}:{body}";
        var secretBytes = Encoding.UTF8.GetBytes(TestSecret);
        var baseBytes   = Encoding.UTF8.GetBytes(baseStr);
        using var hmac  = new System.Security.Cryptography.HMACSHA256(secretBytes);
        var hash = hmac.ComputeHash(baseBytes);
        var sig  = "v0=" + Convert.ToHexString(hash).ToLowerInvariant();
        return (ts, body, sig);
    }

    [Fact]
    public void ValidSignature_ReturnsTrue()
    {
        var (ts, body, sig) = ValidTuple();
        Assert.True(SlackSignatureVerifier.Verify(body, ts, sig, TestSecret));
    }

    [Fact]
    public void TamperedBody_ReturnsFalse()
    {
        var (ts, _, sig) = ValidTuple();
        // Signature was computed for "test=body", but body is now different
        Assert.False(SlackSignatureVerifier.Verify("tampered=body", ts, sig, TestSecret));
    }

    [Fact]
    public void WrongSecret_ReturnsFalse()
    {
        var (ts, body, sig) = ValidTuple();
        Assert.False(SlackSignatureVerifier.Verify(body, ts, sig, "wrong-secret"));
    }

    [Fact]
    public void StaleTimestamp_ReturnsFalse()
    {
        // Timestamp older than 5 minutes
        var staleTs = (DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 400).ToString();
        var body    = "test=body";
        var baseStr = $"v0:{staleTs}:{body}";
        var secret  = Encoding.UTF8.GetBytes(TestSecret);
        var baseB   = Encoding.UTF8.GetBytes(baseStr);
        using var hmac = new System.Security.Cryptography.HMACSHA256(secret);
        var hash = hmac.ComputeHash(baseB);
        var sig  = "v0=" + Convert.ToHexString(hash).ToLowerInvariant();

        Assert.False(SlackSignatureVerifier.Verify(body, staleTs, sig, TestSecret));
    }

    [Fact]
    public void FutureTimestamp_ReturnsFalse()
    {
        // Timestamp 10 minutes in the future
        var futureTs = (DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 600).ToString();
        var body     = "test=body";
        var baseStr  = $"v0:{futureTs}:{body}";
        var secret   = Encoding.UTF8.GetBytes(TestSecret);
        var baseB    = Encoding.UTF8.GetBytes(baseStr);
        using var hmac = new System.Security.Cryptography.HMACSHA256(secret);
        var hash = hmac.ComputeHash(baseB);
        var sig  = "v0=" + Convert.ToHexString(hash).ToLowerInvariant();

        Assert.False(SlackSignatureVerifier.Verify(body, futureTs, sig, TestSecret));
    }

    [Fact]
    public void MissingTimestamp_ReturnsFalse()
    {
        var (_, body, sig) = ValidTuple();
        Assert.False(SlackSignatureVerifier.Verify(body, null, sig, TestSecret));
    }

    [Fact]
    public void MissingSignature_ReturnsFalse()
    {
        var (ts, body, _) = ValidTuple();
        Assert.False(SlackSignatureVerifier.Verify(body, ts, null, TestSecret));
    }

    [Fact]
    public void EmptySecret_ReturnsFalse()
    {
        var (ts, body, sig) = ValidTuple();
        Assert.False(SlackSignatureVerifier.Verify(body, ts, sig, ""));
    }
}

// ── Test HTTP message handlers ────────────────────────────────────────────────

internal sealed class CapturingHandler : HttpMessageHandler
{
    public List<(HttpRequestMessage Req, string Body)> Calls { get; } = new();

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is not null
            ? await request.Content.ReadAsStringAsync(ct)
            : "";
        Calls.Add((request, body));
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"ok\":true}", Encoding.UTF8, "application/json")
        };
    }
}

internal sealed class ThrowingHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken ct)
        => throw new HttpRequestException("Simulated Slack API failure.");
}
