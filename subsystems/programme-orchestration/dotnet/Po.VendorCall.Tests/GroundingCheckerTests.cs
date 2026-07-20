using Po.VendorCall;
using Xunit;

namespace Po.VendorCall.Tests;

public sealed class GroundingCheckerTests
{
    // ── Passes — trivial / no tokens ─────────────────────────────────────────

    [Fact]
    public void Passes_EmptyText_ReturnsTrue()
    {
        var allowed = new HashSet<string>();
        Assert.True(GroundingChecker.Passes("", allowed));
    }

    [Fact]
    public void Passes_TextWithNoCheckableTokens_ReturnsTrue()
    {
        // Small numbers and plain words are never checked
        var text    = "The commitment is 13 days overdue and requires attention.";
        var allowed = new HashSet<string>();
        Assert.True(GroundingChecker.Passes(text, allowed));
    }

    // ── Passes — ISO dates ────────────────────────────────────────────────────

    [Fact]
    public void Passes_KnownDate_ReturnsTrue()
    {
        var text    = "The notice deadline is 2026-07-30.";
        var allowed = new HashSet<string> { "2026-07-30" };
        Assert.True(GroundingChecker.Passes(text, allowed));
    }

    [Fact]
    public void Passes_UnknownDate_ReturnsFalse()
    {
        var text    = "The deadline is 2099-01-01.";
        var allowed = new HashSet<string> { "2026-07-30" };
        Assert.False(GroundingChecker.Passes(text, allowed));
    }

    [Fact]
    public void Passes_MultipleKnownDates_ReturnsTrue()
    {
        var text    = "Renewal is 2026-09-28 with notice by 2026-07-30.";
        var allowed = new HashSet<string> { "2026-09-28", "2026-07-30" };
        Assert.True(GroundingChecker.Passes(text, allowed));
    }

    [Fact]
    public void Passes_OneKnownOneMissingDate_ReturnsFalse()
    {
        var text    = "Renewal is 2026-09-28 with notice by 2026-07-30.";
        var allowed = new HashSet<string> { "2026-09-28" }; // missing 2026-07-30
        Assert.False(GroundingChecker.Passes(text, allowed));
    }

    // ── Passes — large numbers ────────────────────────────────────────────────

    [Fact]
    public void Passes_KnownLargeNumberWithComma_ReturnsTrue()
    {
        var text    = "The annual contract value is £285,000.";
        var allowed = new HashSet<string> { "285000" }; // normalised
        Assert.True(GroundingChecker.Passes(text, allowed));
    }

    [Fact]
    public void Passes_KnownLargeNumberWithoutComma_ReturnsTrue()
    {
        var text    = "The ACV is 285000 GBP.";
        var allowed = new HashSet<string> { "285000" };
        Assert.True(GroundingChecker.Passes(text, allowed));
    }

    [Fact]
    public void Passes_UnknownLargeNumber_ReturnsFalse()
    {
        var text    = "The annual contract value is £300,000.";
        var allowed = new HashSet<string> { "285000" };
        Assert.False(GroundingChecker.Passes(text, allowed));
    }

    // ── BuildAllowed ──────────────────────────────────────────────────────────

    [Fact]
    public void BuildAllowed_ExtractsDatesFromStrings()
    {
        var allowed = GroundingChecker.BuildAllowed(["2026-09-28", "2026-07-30"]);
        Assert.Contains("2026-09-28", allowed);
        Assert.Contains("2026-07-30", allowed);
    }

    [Fact]
    public void BuildAllowed_ExtractsAndNormalisesLargeNumbers()
    {
        // Comma-formatted input → normalised (no comma) in allowed set
        var allowed = GroundingChecker.BuildAllowed(["285,000"]);
        Assert.Contains("285000", allowed);
    }

    [Fact]
    public void BuildAllowed_ExtractsFromMultipleStrings()
    {
        var allowed = GroundingChecker.BuildAllowed(
        [
            "renewal_date: 2026-09-28",
            "annual_value: 285000"
        ]);
        Assert.Contains("2026-09-28", allowed);
        Assert.Contains("285000",     allowed);
    }

    [Fact]
    public void BuildAllowed_SmallNumbersAreNotExtracted()
    {
        // 13, 60 are below the 6-digit / comma-format threshold
        var allowed = GroundingChecker.BuildAllowed(["13 days", "60 day notice period"]);
        Assert.Empty(allowed);
    }

    [Fact]
    public void BuildAllowed_EmptyInput_ReturnsEmptySet()
    {
        var allowed = GroundingChecker.BuildAllowed(Enumerable.Empty<string>());
        Assert.Empty(allowed);
    }
}
