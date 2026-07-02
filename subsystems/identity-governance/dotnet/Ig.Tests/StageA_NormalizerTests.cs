using Ig.Contracts;
using Ig.Resolution;
using Kozmo.Contracts;
using Xunit;

namespace Ig.Tests;

public sealed class StageA_NormalizerTests
{
    // ── Helper ─────────────────────────────────────────────────────────────────

    private static CandidateIdentityBelief Candidate(string rawName) =>
        new CandidateIdentityBelief(
            CandidateId: Guid.NewGuid(),
            RawName:     rawName,
            SourceTier:  SourceTier.Verified,
            Confidence:  0.8,
            Provenance:  new Provenance("doc-1", null, null),
            Signals:     null,
            RoleHint:    null);

    // ── Same comparison key for all CloudWave variants ─────────────────────────

    [Fact]
    public void Normalize_AllCloudWaveVariants_ProduceSameComparisonKey()
    {
        var key1 = Normalizer.Normalize(Candidate("Cloud Wave Inc.")).ComparisonKey;
        var key2 = Normalizer.Normalize(Candidate("CloudWave")).ComparisonKey;
        var key3 = Normalizer.Normalize(Candidate("CLOUDWAVE LLC")).ComparisonKey;

        Assert.Equal(key1, key2);
        Assert.Equal(key2, key3);
    }

    [Fact]
    public void Normalize_CloudWaveInc_KeyIsCloudwave()
    {
        var result = Normalizer.Normalize(Candidate("Cloud Wave Inc."));
        Assert.Equal("cloudwave", result.ComparisonKey);
    }

    [Fact]
    public void Normalize_CamelCase_SplitsBeforeNormalising()
    {
        var result = Normalizer.Normalize(Candidate("CloudWave"));
        Assert.Equal("cloudwave", result.ComparisonKey);
    }

    [Fact]
    public void Normalize_AllCaps_HandledCorrectly()
    {
        var result = Normalizer.Normalize(Candidate("CLOUDWAVE LLC"));
        Assert.Equal("cloudwave", result.ComparisonKey);
    }

    // ── Raw name is preserved exactly ──────────────────────────────────────────

    [Theory]
    [InlineData("Cloud Wave Inc.")]
    [InlineData("CloudWave")]
    [InlineData("CLOUDWAVE LLC")]
    [InlineData("Amendment 3 – Aramark")]
    public void Normalize_RawName_PreservedExactly(string rawName)
    {
        var result = Normalizer.Normalize(Candidate(rawName));
        Assert.Equal(rawName, result.Candidate.RawName);
    }

    // ── Document-title prefix stripping ───────────────────────────────────────

    [Fact]
    public void Normalize_DocumentPrefix_EnDash_StripsPrefix()
    {
        // "Amendment 3 – Aramark" — en-dash U+2013
        var result = Normalizer.Normalize(Candidate("Amendment 3 – Aramark"));

        Assert.Equal("aramark", result.ComparisonKey);
        Assert.Equal("Aramark", result.EffectiveName);
    }

    [Fact]
    public void Normalize_DocumentPrefix_Hyphen_StripsPrefix()
    {
        var result = Normalizer.Normalize(Candidate("SOW - Aramark"));

        Assert.Equal("aramark", result.ComparisonKey);
        Assert.Equal("Aramark", result.EffectiveName);
    }

    [Fact]
    public void Normalize_NoDocumentPrefix_EffectiveNameEqualsRawName()
    {
        var result = Normalizer.Normalize(Candidate("Cloud Wave Inc."));
        Assert.Equal("Cloud Wave Inc.", result.EffectiveName);
    }

    // ── Legal suffix and noise word removal ───────────────────────────────────

    [Theory]
    [InlineData("Acme Inc.",    "acme")]
    [InlineData("Acme LLC",     "acme")]
    [InlineData("Acme Ltd",     "acme")]
    [InlineData("Acme Limited", "acme")]
    [InlineData("Acme Corp",    "acme")]
    [InlineData("Acme GmbH",    "acme")]
    [InlineData("Acme Co",      "acme")]
    public void Normalize_LegalSuffix_IsStripped(string rawName, string expectedKey)
    {
        var result = Normalizer.Normalize(Candidate(rawName));
        Assert.Equal(expectedKey, result.ComparisonKey);
    }

    [Fact]
    public void Normalize_NoiseWords_AreStripped()
    {
        // "The Bank of England" → "bankengland"
        var result = Normalizer.Normalize(Candidate("The Bank of England"));
        Assert.Equal("bankengland", result.ComparisonKey);
    }

    // ── "Company" suffix — same key as "Co." ──────────────────────────────────

    [Theory]
    [InlineData("Acme Company", "acme")]
    [InlineData("Widget Company Ltd", "widget")]
    public void Normalize_CompanySuffix_IsStripped(string rawName, string expectedKey)
    {
        var result = Normalizer.Normalize(Candidate(rawName));
        Assert.Equal(expectedKey, result.ComparisonKey);
    }

    [Fact]
    public void Normalize_TravelersCompanyVariants_ProduceSameKey()
    {
        // "Company" and "Co." must collapse to the same key after Stage A.
        var key1 = Normalizer.Normalize(Candidate("Travelers Property Casualty Company of America")).ComparisonKey;
        var key2 = Normalizer.Normalize(Candidate("Travelers Property Casualty Co. of America")).ComparisonKey;
        Assert.Equal(key1, key2);
        Assert.Equal("travelerspropertycasualtyamerica", key1);
    }
}
