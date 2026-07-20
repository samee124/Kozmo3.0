using Po.VendorCall;
using Xunit;

namespace Po.VendorCall.Tests;

public sealed class MatcherTests
{
    private static readonly Guid AcmeId    = Guid.Parse("aaaaaaaa-0001-0000-0000-000000000000");
    private static readonly Guid BetaId    = Guid.Parse("bbbbbbbb-0001-0000-0000-000000000000");

    private static readonly VendorMatch AcmeCorp = new(
        VendorId:    AcmeId,
        VendorName:  "Acme Corp",
        KnownDomains: ["acme.com"],
        Aliases:     ["acme-corp", "acmecorp"]);

    private static readonly VendorMatch BetaSystems = new(
        VendorId:    BetaId,
        VendorName:  "betasystems",
        KnownDomains: ["betasys.io"],
        Aliases:     ["beta-sys"]);

    private static VendorCallEntityMatcher Matcher(params VendorMatch[] vendors)
        => new(new InMemoryVendorLookup(vendors));

    // ── Domain exact ──────────────────────────────────────────────────────────

    [Fact]
    public async Task MatchAsync_DomainExactMatch_ReturnsDomainExact()
    {
        var results = await Matcher(AcmeCorp).MatchAsync(["alice@acme.com"]);
        var r = Assert.Single(results);
        Assert.Equal(AcmeId,                r.VendorId);
        Assert.Equal(VendorMatchType.DomainExact, r.MatchType);
        Assert.Equal(0.95,                  r.MatchScore);
    }

    // ── Name exact fallback ───────────────────────────────────────────────────

    [Fact]
    public async Task MatchAsync_NameExactFallback_ReturnsNameExact()
    {
        // BetaSystems has domain "betasys.io"; attendee is from "betasystems.com"
        // domain lookup fails, stem = "betasystems", name match succeeds.
        var results = await Matcher(BetaSystems).MatchAsync(["bob@betasystems.com"]);
        var r = Assert.Single(results);
        Assert.Equal(BetaId,                    r.VendorId);
        Assert.Equal(VendorMatchType.NameExact, r.MatchType);
        Assert.Equal(0.85,                      r.MatchScore);
    }

    // ── Alias fallback ────────────────────────────────────────────────────────

    [Fact]
    public async Task MatchAsync_AliasFallback_ReturnsAlias()
    {
        // Acme has no domain "acme-corp.com"; stem = "acme-corp", alias match.
        var results = await Matcher(AcmeCorp).MatchAsync(["sales@acme-corp.com"]);
        var r = Assert.Single(results);
        Assert.Equal(AcmeId,                r.VendorId);
        Assert.Equal(VendorMatchType.Alias, r.MatchType);
        Assert.Equal(0.80,                  r.MatchScore);
    }

    // ── Unmatched ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task MatchAsync_NoMatch_ReturnsUnmatched()
    {
        var results = await Matcher(AcmeCorp).MatchAsync(["unknown@mystery.org"]);
        var r = Assert.Single(results);
        Assert.Equal(Guid.Empty,                 r.VendorId);
        Assert.Equal(VendorMatchType.Unmatched,  r.MatchType);
        Assert.Equal(0.0,                        r.MatchScore);
        Assert.Equal("mystery.org",              r.VendorName);
    }

    // ── Multiple vendors ──────────────────────────────────────────────────────

    [Fact]
    public async Task MatchAsync_MultipleVendorDomains_ReturnsOnePerDomain()
    {
        var results = await Matcher(AcmeCorp, BetaSystems)
            .MatchAsync(["alice@acme.com", "bob@betasys.io"]);
        Assert.Equal(2, results.Count);
        Assert.Contains(results, r => r.VendorId == AcmeId && r.MatchType == VendorMatchType.DomainExact);
        Assert.Contains(results, r => r.VendorId == BetaId && r.MatchType == VendorMatchType.DomainExact);
    }

    // ── Deduplication by vendor ───────────────────────────────────────────────

    [Fact]
    public async Task MatchAsync_SameVendorTwoAttendees_OnlyOneResult()
    {
        var results = await Matcher(AcmeCorp)
            .MatchAsync(["alice@acme.com", "bob@acme.com"]);
        var r = Assert.Single(results);
        Assert.Equal(AcmeId, r.VendorId);
    }

    // ── Empty input ───────────────────────────────────────────────────────────

    [Fact]
    public async Task MatchAsync_EmptyEmails_ReturnsEmpty()
    {
        var results = await Matcher(AcmeCorp).MatchAsync([]);
        Assert.Empty(results);
    }
}
