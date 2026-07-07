using Wc.CheckIn;
using Xunit;

namespace Wc.Tests;

public sealed class CheckInLinkTokenTests
{
    private const string Secret  = "test-secret-32-bytes-of-entropy!";
    private const int    TtlDays = 7;

    private static readonly Guid           CheckInId = new("AAAAAAAA-0000-0000-0000-000000000001");
    private static readonly DateTimeOffset Now       = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    // ── A. Round-trip: Generate → TryValidate succeeds ─────────────────────

    [Theory]
    [InlineData("YES")]
    [InlineData("NO")]
    [InlineData("UNKNOWN")]
    public void RoundTrip_ValidToken_ReturnsCorrectFields(string value)
    {
        var token = CheckInLinkToken.Generate(CheckInId, value, Secret, TtlDays, Now);
        var ok    = CheckInLinkToken.TryValidate(token, Secret, Now, out var id, out var v);

        Assert.True(ok);
        Assert.Equal(CheckInId, id);
        Assert.Equal(value,     v);
    }

    // ── B. Token format: two dot-separated Base64Url segments ───────────────

    [Fact]
    public void Generate_ProducesTokenWithOneDot()
    {
        var token = CheckInLinkToken.Generate(CheckInId, "YES", Secret, TtlDays, Now);
        Assert.Equal(1, token.Count(c => c == '.'));
    }

    [Fact]
    public void Generate_UsesBase64UrlEncoding_NoPaddingOrPlusSlash()
    {
        var token = CheckInLinkToken.Generate(CheckInId, "YES", Secret, TtlDays, Now);
        Assert.DoesNotContain('+', token);
        Assert.DoesNotContain('/', token);
        Assert.DoesNotContain('=', token);
    }

    // ── C. Wrong secret → validation fails ──────────────────────────────────

    [Fact]
    public void TryValidate_WrongSecret_ReturnsFalse()
    {
        var token = CheckInLinkToken.Generate(CheckInId, "YES", Secret, TtlDays, Now);
        var ok    = CheckInLinkToken.TryValidate(token, "wrong-secret", Now, out _, out _);
        Assert.False(ok);
    }

    // ── D. Payload tamper → validation fails ────────────────────────────────

    [Fact]
    public void TryValidate_TamperedPayload_ReturnsFalse()
    {
        var token  = CheckInLinkToken.Generate(CheckInId, "YES", Secret, TtlDays, Now);
        var dot    = token.IndexOf('.');
        var sig    = token[(dot + 1)..];

        // Rebuild with different payload, keep original signature
        var badPayload = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("tampered|YES|2099-01-01T00:00:00Z"))
                                .Replace('+', '-').Replace('/', '_').TrimEnd('=');
        var tampered = $"{badPayload}.{sig}";

        var ok = CheckInLinkToken.TryValidate(tampered, Secret, Now, out _, out _);
        Assert.False(ok);
    }

    // ── E. Expired token → validation fails ─────────────────────────────────

    [Fact]
    public void TryValidate_ExpiredToken_ReturnsFalse()
    {
        var token  = CheckInLinkToken.Generate(CheckInId, "YES", Secret, TtlDays, Now);
        var future = Now.AddDays(TtlDays + 1);
        var ok     = CheckInLinkToken.TryValidate(token, Secret, future, out _, out _);
        Assert.False(ok);
    }

    // ── F. Token valid at exact expiry boundary ──────────────────────────────

    [Fact]
    public void TryValidate_AtExactExpiry_ReturnsTrue()
    {
        // The check is `now > expiresAt`; at the exact boundary now == expiresAt → not expired.
        var token  = CheckInLinkToken.Generate(CheckInId, "YES", Secret, TtlDays, Now);
        var expiry = Now.AddDays(TtlDays);
        var ok     = CheckInLinkToken.TryValidate(token, Secret, expiry, out _, out _);
        Assert.True(ok);
    }

    [Fact]
    public void TryValidate_OneSecondBeforeExpiry_ReturnsTrue()
    {
        var token      = CheckInLinkToken.Generate(CheckInId, "YES", Secret, TtlDays, Now);
        var justBefore = Now.AddDays(TtlDays).AddSeconds(-1);
        var ok         = CheckInLinkToken.TryValidate(token, Secret, justBefore, out _, out _);
        Assert.True(ok);
    }

    // ── G. Malformed tokens ──────────────────────────────────────────────────

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("nodot")]
    [InlineData("only.one")]         // payload decodes but splits wrong
    [InlineData("!@#$.!@#$")]        // invalid base64
    public void TryValidate_MalformedToken_ReturnsFalse(string bad)
    {
        var ok = CheckInLinkToken.TryValidate(bad, Secret, Now, out _, out _);
        Assert.False(ok);
    }

    // ── H. Route-mismatch guard: different tokens for different check-in IDs ─

    [Fact]
    public void Generate_DifferentCheckInIds_ProduceDifferentTokens()
    {
        var id1 = new Guid("AAAAAAAA-0000-0000-0000-000000000001");
        var id2 = new Guid("AAAAAAAA-0000-0000-0000-000000000002");

        var t1 = CheckInLinkToken.Generate(id1, "YES", Secret, TtlDays, Now);
        var t2 = CheckInLinkToken.Generate(id2, "YES", Secret, TtlDays, Now);

        Assert.NotEqual(t1, t2);
    }

    [Fact]
    public void TryValidate_TokenForDifferentId_ExtractsWrongId()
    {
        // A valid token for id1 should NOT validate when the route id is id2.
        // (The page model checks tokenId == routeId after TryValidate.)
        var id1   = new Guid("AAAAAAAA-0000-0000-0000-000000000001");
        var id2   = new Guid("AAAAAAAA-0000-0000-0000-000000000002");
        var token = CheckInLinkToken.Generate(id1, "YES", Secret, TtlDays, Now);

        var ok = CheckInLinkToken.TryValidate(token, Secret, Now, out var extractedId, out _);
        Assert.True(ok);
        Assert.Equal(id1,  extractedId);
        Assert.NotEqual(id2, extractedId); // route mismatch would be caught at call site
    }

    // ── I. Different values produce different tokens ──────────────────────────

    [Fact]
    public void Generate_DifferentValues_ProduceDifferentTokens()
    {
        var t1 = CheckInLinkToken.Generate(CheckInId, "YES", Secret, TtlDays, Now);
        var t2 = CheckInLinkToken.Generate(CheckInId, "NO",  Secret, TtlDays, Now);
        Assert.NotEqual(t1, t2);
    }
}
