using Po.VendorCall;
using Xunit;

namespace Po.VendorCall.Tests;

/// <summary>
/// End-to-end simulation of the Northstar Software scenario observed during
/// Phase 4 mail-source harness testing (daniel@northstarsoftware.com, renewal pricing).
/// </summary>
public sealed class NorthstarIntegrationTests
{
    private static readonly Guid NorthstarId = Guid.Parse("cccccccc-0001-0000-0000-000000000000");

    private static readonly VendorMatch Northstar = new(
        VendorId:    NorthstarId,
        VendorName:  "Northstar Software",
        KnownDomains: ["northstarsoftware.com"],
        Aliases:     ["northstarsoftware", "North Star Software"]);

    private static VendorCallRecognitionConfig Config() => new(
        AutoRelevantThreshold:   0.85,
        ReviewRelevantThreshold: 0.55,
        InternalDomains:         ["econtracts.onmicrosoft.com"],
        TitleTerms:              ["renewal", "contract", "pricing", "sla", "agreement", "commercial"],
        BodyTerms:               ["uplift", "discount", "renewal date", "contract term", "invoice"]);

    // ── Recognizer ────────────────────────────────────────────────────────────

    [Fact]
    public void NorthstarRenewal_Recognizer_IsRelevantWithReview()
    {
        var recognizer = new VendorCallRecognizer(Config());
        var result = recognizer.Recognize(
            title:         "Renewal pricing discussion",
            body:          "We expect the renewal to reflect a 7% uplift.",
            attendeeEmails:
            [
                "rishi@econtracts.onmicrosoft.com",   // internal
                "daniel@northstarsoftware.com"         // external vendor
            ]);

        Assert.True(result.IsRelevant);
        Assert.Single(result.ExternalAttendees);
        Assert.Equal("daniel@northstarsoftware.com", result.ExternalAttendees[0]);
        // "renewal" in title (+0.2) + "pricing" in title (+0.1) = +0.3 → 0.8; "uplift" in body (+0.05) → 0.85 → auto
        Assert.False(result.RequiresReview);
        Assert.Equal(0.85, result.Confidence, precision: 10);
        Assert.Contains("renewal", result.MatchedTitleTerms);
        Assert.Contains("pricing", result.MatchedTitleTerms);
        Assert.Contains("uplift",  result.MatchedBodyTerms);
    }

    // ── Matcher ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task NorthstarRenewal_Matcher_FindsVendorByDomainExact()
    {
        var matcher = new VendorCallEntityMatcher(new InMemoryVendorLookup([Northstar]));
        var results = await matcher.MatchAsync(["daniel@northstarsoftware.com"]);
        var r = Assert.Single(results);
        Assert.Equal(NorthstarId,                 r.VendorId);
        Assert.Equal("Northstar Software",        r.VendorName);
        Assert.Equal(VendorMatchType.DomainExact, r.MatchType);
        Assert.Equal(0.95,                        r.MatchScore);
    }

    // ── Combined recognizer + matcher ─────────────────────────────────────────

    [Fact]
    public async Task NorthstarRenewal_EndToEnd_RelevantAndMatched()
    {
        var recognizer = new VendorCallRecognizer(Config());
        var recognitionResult = recognizer.Recognize(
            title:         "Renewal pricing discussion",
            body:          "We expect the renewal to reflect a 7% uplift.",
            attendeeEmails: ["rishi@econtracts.onmicrosoft.com", "daniel@northstarsoftware.com"]);

        Assert.True(recognitionResult.IsRelevant);

        var matcher      = new VendorCallEntityMatcher(new InMemoryVendorLookup([Northstar]));
        var matchResults = await matcher.MatchAsync(recognitionResult.ExternalAttendees);

        var match = Assert.Single(matchResults);
        Assert.Equal(NorthstarId, match.VendorId);
        Assert.Equal(VendorMatchType.DomainExact, match.MatchType);
    }
}
