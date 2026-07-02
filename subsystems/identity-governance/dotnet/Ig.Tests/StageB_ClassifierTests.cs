using Ig.Contracts;
using Ig.Resolution;
using Kozmo.Contracts;
using Xunit;

namespace Ig.Tests;

public sealed class StageB_ClassifierTests
{
    // ── Helpers ────────────────────────────────────────────────────────────────

    private static NormalizedCandidate MakeNormalized(string rawName)
    {
        var candidate = new CandidateIdentityBelief(
            CandidateId: Guid.NewGuid(),
            RawName:     rawName,
            SourceTier:  SourceTier.Verified,
            Confidence:  0.8,
            Provenance:  new Provenance("doc-1", null, null),
            Signals:     null,
            RoleHint:    null);

        return Normalizer.Normalize(candidate);
    }

    private static EntityTypeClassificationStage Stage(
        EntityType llmFallback = EntityType.Unknown)
        => new(new FakeEntityTypeClassifier(llmFallback));

    // ── PERSON ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Classify_JohnSmith_IsPersonAndDropped()
    {
        var fake  = new FakeEntityTypeClassifier();
        var stage = new EntityTypeClassificationStage(fake);
        var norm  = MakeNormalized("John Smith");

        var result = await stage.ClassifyAsync(norm);

        Assert.Equal(EntityType.Person, result.EntityType);
        Assert.True(result.IsDropped);
        Assert.NotNull(result.DropReason);
        Assert.Equal(0, fake.CallCount); // deterministic — no LLM needed
    }

    [Theory]
    [InlineData("Jane Doe")]
    [InlineData("Robert Johnson")]
    [InlineData("Mary A. Williams")]
    public async Task Classify_PersonNames_AreAllDropped(string name)
    {
        var result = await Stage().ClassifyAsync(MakeNormalized(name));

        Assert.Equal(EntityType.Person, result.EntityType);
        Assert.True(result.IsDropped);
    }

    // ── INTERNAL ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Classify_ITProcurement_IsInternalAndDropped()
    {
        var fake  = new FakeEntityTypeClassifier();
        var stage = new EntityTypeClassificationStage(fake);
        var norm  = MakeNormalized("IT Procurement");

        var result = await stage.ClassifyAsync(norm);

        Assert.Equal(EntityType.Internal, result.EntityType);
        Assert.True(result.IsDropped);
        Assert.NotNull(result.DropReason);
        Assert.Equal(0, fake.CallCount); // deterministic — no LLM needed
    }

    [Theory]
    [InlineData("Finance")]
    [InlineData("HR")]
    [InlineData("Procurement")]
    [InlineData("Accounting")]
    [InlineData("Legal")]
    [InlineData("IT")]
    public async Task Classify_InternalKeywordsStandalone_AreDropped(string name)
    {
        var result = await Stage().ClassifyAsync(MakeNormalized(name));

        Assert.Equal(EntityType.Internal, result.EntityType);
        Assert.True(result.IsDropped);
    }

    // ── Document-title trap (Amendment 3 – Aramark) ───────────────────────────

    [Fact]
    public void Normalize_DocumentPrefix_EnDash_EffectiveNameIsAramark()
    {
        // Stage A strips "Amendment 3 – " prefix, leaving EffectiveName = "Aramark"
        var norm = MakeNormalized("Amendment 3 – Aramark");

        Assert.Equal("Aramark", norm.EffectiveName);
        Assert.Equal("aramark", norm.ComparisonKey);
    }

    [Fact]
    public async Task Classify_Aramark_IsCompanyAndProceeds()
    {
        // "Aramark" by itself: single word, not PERSON, not INTERNAL → COMPANY by default
        var result = await Stage().ClassifyAsync(MakeNormalized("Aramark"));

        Assert.False(result.IsDropped);
        Assert.True(result.EntityType is EntityType.Company or EntityType.Unknown);
    }

    [Fact]
    public async Task Classify_AmendmentAramark_EffectiveNameClassifiedAsCompany()
    {
        // Full end-to-end: Normalize then Classify on the document-title input
        var norm   = MakeNormalized("Amendment 3 – Aramark");
        var result = await Stage().ClassifyAsync(norm);

        Assert.False(result.IsDropped);
        Assert.True(result.EntityType is EntityType.Company or EntityType.Unknown);
    }

    // ── COMPANY and UNKNOWN proceed ────────────────────────────────────────────

    [Theory]
    [InlineData("Cloud Wave Inc.")]
    [InlineData("CloudWave")]
    [InlineData("CLOUDWAVE LLC")]
    [InlineData("Meridian IT Services Ltd.")]
    public async Task Classify_CompanyNames_AreNotDropped(string name)
    {
        var result = await Stage().ClassifyAsync(MakeNormalized(name));

        Assert.False(result.IsDropped);
        Assert.Null(result.DropReason);
    }

    // ── Drop cases carry a reason ──────────────────────────────────────────────

    [Theory]
    [InlineData("John Smith",    EntityType.Person)]
    [InlineData("IT Procurement",EntityType.Internal)]
    public async Task Classify_DroppedCandidates_CarryNonNullDropReason(
        string name, EntityType expectedType)
    {
        var result = await Stage().ClassifyAsync(MakeNormalized(name));

        Assert.Equal(expectedType, result.EntityType);
        Assert.True(result.IsDropped);
        Assert.False(string.IsNullOrWhiteSpace(result.DropReason));
    }

    // ── LLM is NOT called for deterministic cases ──────────────────────────────

    [Theory]
    [InlineData("John Smith")]
    [InlineData("IT Procurement")]
    [InlineData("Aramark")]
    [InlineData("Cloudwave Solutions")]
    public async Task Classify_DeterministicCases_NeverCallLlm(string name)
    {
        var fake  = new FakeEntityTypeClassifier();
        var stage = new EntityTypeClassificationStage(fake);

        await stage.ClassifyAsync(MakeNormalized(name));

        Assert.Equal(0, fake.CallCount);
    }

    // ── Legal-suffix gate (additive fix) ───────────────────────────────────────

    /// <summary>
    /// A multi-word name carrying a legal suffix is classified COMPANY by rule,
    /// never falling through to the LLM. Verifies the fix is purely additive:
    /// PERSON / INTERNAL paths remain unaffected (they fire earlier in the switch).
    /// </summary>
    [Theory]
    [InlineData("Cloud Wave Inc.")]
    [InlineData("CLOUDWAVE LLC")]
    [InlineData("Meridian IT Services Ltd.")]
    public async Task Classify_LegalSuffixNames_AreCompanyWithoutLlm(string name)
    {
        var fake  = new FakeEntityTypeClassifier();
        var stage = new EntityTypeClassificationStage(fake);

        var result = await stage.ClassifyAsync(MakeNormalized(name));

        Assert.Equal(EntityType.Company, result.EntityType);
        Assert.False(result.IsDropped);
        Assert.Equal(0, fake.CallCount); // legal suffix → Company by rule; no LLM needed
    }
}
