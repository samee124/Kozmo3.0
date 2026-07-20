using Po.VendorCall;
using Xunit;

namespace Po.VendorCall.Tests;

public sealed class RecognizerTests
{
    // ── Shared config ─────────────────────────────────────────────────────────

    private static VendorCallRecognitionConfig Config() => new(
        AutoRelevantThreshold:   0.85,
        ReviewRelevantThreshold: 0.55,
        InternalDomains:         ["internal.example.com"],
        TitleTerms:              ["renewal", "contract", "pricing"],
        BodyTerms:               ["uplift", "discount", "invoice"]);

    private static VendorCallRecognizer Recognizer() => new(Config());

    // ── No external attendees ─────────────────────────────────────────────────

    [Fact]
    public void Recognize_NoAttendees_IsNotRelevant_ConfidenceOne()
    {
        var result = Recognizer().Recognize("Renewal call", "body", []);
        Assert.False(result.IsRelevant);
        Assert.False(result.RequiresReview);
        Assert.Equal(1.0, result.Confidence);
        Assert.Empty(result.ExternalAttendees);
    }

    [Fact]
    public void Recognize_OnlyInternalAttendees_IsNotRelevant()
    {
        var result = Recognizer().Recognize(
            "Renewal call", "body",
            ["alice@internal.example.com", "bob@internal.example.com"]);
        Assert.False(result.IsRelevant);
        Assert.Equal(1.0, result.Confidence);
        Assert.Empty(result.ExternalAttendees);
    }

    // ── Base score with external attendees ────────────────────────────────────

    [Fact]
    public void Recognize_ExternalAttendee_NoKeywords_BaseIsHalf_NotRelevant()
    {
        var result = Recognizer().Recognize(
            "Team sync", "",
            ["vendor@external.com"]);
        Assert.False(result.IsRelevant);
        Assert.Equal(0.5, result.Confidence);
        Assert.Single(result.ExternalAttendees);
    }

    // ── Title term scoring ────────────────────────────────────────────────────

    [Fact]
    public void Recognize_OneTitleTerm_ConfidenceIsSeventy()
    {
        var result = Recognizer().Recognize(
            "Renewal discussion", "",
            ["vendor@external.com"]);
        Assert.True(result.IsRelevant);
        Assert.True(result.RequiresReview);
        Assert.Equal(0.7, result.Confidence, precision: 10);
        var term = Assert.Single(result.MatchedTitleTerms);
        Assert.Equal("renewal", term);
    }

    [Fact]
    public void Recognize_TwoTitleTerms_ConfidenceIsEighty()
    {
        var result = Recognizer().Recognize(
            "Renewal contract review", "",
            ["vendor@external.com"]);
        Assert.True(result.IsRelevant);
        Assert.True(result.RequiresReview);
        Assert.Equal(0.8, result.Confidence, precision: 10);
        Assert.Equal(2, result.MatchedTitleTerms.Count);
    }

    [Fact]
    public void Recognize_ThreeTitleTerms_TitleBonusCappedAt0_3()
    {
        // "renewal", "contract", "pricing" all match — bonus capped at 0.3
        var result = Recognizer().Recognize(
            "Renewal contract pricing call", "",
            ["vendor@external.com"]);
        Assert.Equal(0.8, result.Confidence, precision: 10); // 0.5 + 0.3
        Assert.Equal(3, result.MatchedTitleTerms.Count);
    }

    // ── Body term scoring ─────────────────────────────────────────────────────

    [Fact]
    public void Recognize_ThreeTitlePlusOneBody_AutoRelevant()
    {
        // 0.5 + 0.3 (title cap) + 0.05 (1 body) = 0.85 → auto
        var result = Recognizer().Recognize(
            "Renewal contract pricing call",
            "Expect an uplift of 7%.",
            ["vendor@external.com"]);
        Assert.True(result.IsRelevant);
        Assert.False(result.RequiresReview);
        Assert.Equal(0.85, result.Confidence, precision: 10);
    }

    [Fact]
    public void Recognize_BodyBonusCappedAtFifteen()
    {
        // 3 body terms × 0.05 = 0.15 (hit cap), 0 title → 0.5 + 0.15 = 0.65
        var result = Recognizer().Recognize(
            "Team sync",
            "Discuss uplift, discount, and invoice settlement.",
            ["vendor@external.com"]);
        Assert.Equal(0.65, result.Confidence, precision: 10);
        Assert.Equal(3, result.MatchedBodyTerms.Count);
    }

    // ── Mixed attendees ───────────────────────────────────────────────────────

    [Fact]
    public void Recognize_MixedAttendees_OnlyExternalContribute()
    {
        var result = Recognizer().Recognize(
            "Team sync", "",
            ["alice@internal.example.com", "vendor@external.com"]);
        Assert.False(result.IsRelevant); // base 0.5, no keywords
        var ext = Assert.Single(result.ExternalAttendees);
        Assert.Equal("vendor@external.com", ext);
    }

    [Fact]
    public void Recognize_DuplicateExternalEmails_Deduplicated()
    {
        var result = Recognizer().Recognize(
            "Team sync", "",
            ["vendor@external.com", "VENDOR@external.com"]);
        Assert.Single(result.ExternalAttendees);
    }

    // ── Matched terms populated ───────────────────────────────────────────────

    [Fact]
    public void Recognize_MatchedTerms_AreReportedInResult()
    {
        var result = Recognizer().Recognize(
            "Renewal pricing",
            "Invoice settlement expected.",
            ["vendor@external.com"]);
        Assert.Contains("renewal", result.MatchedTitleTerms);
        Assert.Contains("pricing", result.MatchedTitleTerms);
        Assert.Contains("invoice", result.MatchedBodyTerms);
    }
}
