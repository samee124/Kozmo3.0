using Ii.CandidateExtraction;
using Xunit;

namespace Ii.Tests;

/// <summary>
/// Phase 2 Commit 1 — deterministic post-filter tests.
/// Test contract: the EXACT junk strings from the Phase 1 real-folder diagnostic.
/// All four IIVS structure-prefixed variants must clean to the SAME name so they
/// cluster as ONE vendor downstream. Real vendor names must pass unchanged.
/// </summary>
public sealed class CandidateFilterTests
{
    private static readonly CandidateFilter Filter = new();

    // ── §5 Case 1: IIVS de-fragmentation ──────────────────────────────────────
    // All four structure-prefixed IIVS variants must clean to the identical string.

    private const string IivsClean = "Institute for In Vitro Sciences, Inc";

    [Theory]
    [InlineData("FROM BILL TO Institute for In Vitro Sciences, Inc")]
    [InlineData("BANKING FORM Institute for In Vitro Sciences, Inc")]
    [InlineData("CERTIFICATE Institute for In Vitro Sciences, Inc")]
    [InlineData("Information Legal Name Institute for In Vitro Sciences, Inc")]
    public void Filter_IivsStructurePrefixedVariants_AllCleanToSameName(string raw)
    {
        var outcome = Filter.Apply(raw);

        Assert.Equal(FilterVerdict.PrefixStripped, outcome.Verdict);
        Assert.Equal(IivsClean, outcome.CleanedName);
        Assert.Null(outcome.DropReason);
    }

    [Fact]
    public void Filter_AllFourIivsVariants_ProduceSameCleanedName()
    {
        // The clustering checkpoint: all four variants clean to the identical string,
        // so Stage A will produce the same comparison_key for all of them.
        var variants = new[]
        {
            "FROM BILL TO Institute for In Vitro Sciences, Inc",
            "BANKING FORM Institute for In Vitro Sciences, Inc",
            "CERTIFICATE Institute for In Vitro Sciences, Inc",
            "Information Legal Name Institute for In Vitro Sciences, Inc",
        };
        var cleanedNames = variants.Select(v => Filter.Apply(v).CleanedName).ToList();

        Assert.All(cleanedNames, name => Assert.Equal(IivsClean, name));
    }

    // ── §5 Case 3: W9 checkbox junk ────────────────────────────────────────────

    [Theory]
    [InlineData("C Corp n S Corp")]
    [InlineData("Corp n LLC")]
    [InlineData("Corp n S Corp")]
    [InlineData("Federal tax classification n Individual n C Corp")]
    public void Filter_W9CheckboxText_Dropped(string raw)
    {
        var outcome = Filter.Apply(raw);

        Assert.Equal(FilterVerdict.Dropped, outcome.Verdict);
        Assert.Null(outcome.CleanedName);
        Assert.NotNull(outcome.DropReason);
    }

    [Fact]
    public void Filter_NonprofitCorporation_Dropped()
    {
        var outcome = Filter.Apply("Nonprofit Corporation");

        Assert.Equal(FilterVerdict.Dropped, outcome.Verdict);
        Assert.Null(outcome.CleanedName);
        Assert.NotNull(outcome.DropReason);
    }

    // ── §5 Case 4: Meeting-note table junk ────────────────────────────────────

    [Fact]
    public void Filter_MeetingNoteTableText_Dropped()
    {
        var outcome = Filter.Apply("Attendees Name Organisation Role David Chen Regulus Group, LLC");

        Assert.Equal(FilterVerdict.Dropped, outcome.Verdict);
        Assert.Null(outcome.CleanedName);
        Assert.NotNull(outcome.DropReason);
    }

    // ── §5 Case 5: Real vendor names pass unchanged (two-sided checkpoint) ─────

    [Theory]
    [InlineData("Aequitas, Inc.")]
    [InlineData("Regulus Group, LLC")]
    [InlineData("ABC Technologies LLC")]
    [InlineData("Institute for In Vitro Sciences, Inc")]
    [InlineData("Revolution Medicines, Inc")]
    [InlineData("The Prudential Insurance Company of America")]
    [InlineData("Biogen Idec U.S. West Corporation")]
    [InlineData("Meridian Health Systems, Inc")]
    public void Filter_RealVendorNames_PassUnchanged(string rawName)
    {
        var outcome = Filter.Apply(rawName);

        Assert.Equal(FilterVerdict.Accepted, outcome.Verdict);
        Assert.Equal(rawName.Trim(), outcome.CleanedName);
        Assert.Null(outcome.DropReason);
    }

    // ── Additional structure-prefix coverage from the diagnostic ──────────────

    [Theory]
    [InlineData("INVOICE Institute for In Vitro Sciences, Inc",       "Institute for In Vitro Sciences, Inc")]
    [InlineData("QBR Institute for In Vitro Sciences, Inc",            "Institute for In Vitro Sciences, Inc")]
    [InlineData("TAX FORM Institute for In Vitro Sciences, Inc",       "Institute for In Vitro Sciences, Inc")]
    [InlineData("VENDOR PROFILE Institute for In Vitro Sciences, Inc", "Institute for In Vitro Sciences, Inc")]
    [InlineData("AMENDMENT Institute for In Vitro Sciences, Inc",      "Institute for In Vitro Sciences, Inc")]
    [InlineData("MASTER SERVICES AGREEMENT Institute for In Vitro Sciences, Inc",
                                                                       "Institute for In Vitro Sciences, Inc")]
    [InlineData("Checking Account Name Institute for In Vitro Sciences, Inc",
                                                                       "Institute for In Vitro Sciences, Inc")]
    [InlineData("Overview Legal Name Institute for In Vitro Sciences, Inc",
                                                                       "Institute for In Vitro Sciences, Inc")]
    [InlineData("Vendor Identification Legal Entity Name Regulus Group, LLC", "Regulus Group, LLC")]
    [InlineData("Submitted By Regulus Group, LLC",                     "Regulus Group, LLC")]
    [InlineData("About Regulus Group, LLC",                            "Regulus Group, LLC")]
    [InlineData("VENDOR CLIENT N Aequitas, Inc",                       "Aequitas, Inc")]
    public void Filter_StructurePrefixed_StripsToRealOrgName(string raw, string expected)
    {
        var outcome = Filter.Apply(raw);

        Assert.Equal(FilterVerdict.PrefixStripped, outcome.Verdict);
        Assert.Equal(expected, outcome.CleanedName);
    }

    // ── Dedup collapses same cleaned name within a document ────────────────────

    [Fact]
    public void Filter_ApplyAndDedup_CollapsesDuplicateCleanedNames()
    {
        // All four IIVS variants clean to the same name — dedup keeps only the first.
        var names = new[]
        {
            "FROM BILL TO Institute for In Vitro Sciences, Inc",
            "INVOICE Institute for In Vitro Sciences, Inc",
            "Institute for In Vitro Sciences, Inc",
            "CERTIFICATE Institute for In Vitro Sciences, Inc",
        };

        var outcomes = Filter.ApplyAndDedup(names);
        var accepted = outcomes.Where(o => o.Verdict != FilterVerdict.Dropped).ToList();

        Assert.Single(accepted);
        Assert.Equal(IivsClean, accepted[0].CleanedName);
    }

    [Fact]
    public void Filter_ApplyAndDedup_DroppedOutcomesIncluded()
    {
        // Dropped entries are always in the result (for diagnostics), even when deduplicating.
        var names = new[]
        {
            "C Corp n S Corp",
            "Aequitas, Inc.",
            "C Corp n S Corp",  // second occurrence — also dropped and included
        };

        var outcomes = Filter.ApplyAndDedup(names);
        var dropped  = outcomes.Where(o => o.Verdict == FilterVerdict.Dropped).ToList();

        Assert.Equal(2, dropped.Count);
        Assert.All(dropped, d => Assert.Equal("C Corp n S Corp", d.RawInput));
    }

    // ── Edge cases ─────────────────────────────────────────────────────────────

    [Fact]
    public void Filter_EmptyString_Dropped()
    {
        Assert.Equal(FilterVerdict.Dropped, Filter.Apply("").Verdict);
        Assert.Equal(FilterVerdict.Dropped, Filter.Apply("   ").Verdict);
    }

    [Fact]
    public void Filter_PrefixWithoutRemainder_Dropped()
    {
        // "INVOICE" with no org name after it must drop, not return an empty cleaned name.
        var outcome = Filter.Apply("INVOICE");

        Assert.Equal(FilterVerdict.Dropped, outcome.Verdict);
        Assert.Null(outcome.CleanedName);
    }

    [Fact]
    public void Filter_PrefixWordBoundary_NotMatchedInsideWord()
    {
        // "INVOICENT Corp" must NOT strip "INVOICE" (no word boundary after "INVOICE").
        var outcome = Filter.Apply("INVOICENT Corp");

        Assert.Equal(FilterVerdict.Accepted, outcome.Verdict);
        Assert.Equal("INVOICENT Corp", outcome.CleanedName);
    }
}
