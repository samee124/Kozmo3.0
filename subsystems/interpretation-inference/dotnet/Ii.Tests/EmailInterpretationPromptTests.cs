using Ii.CandidateExtraction;
using Xunit;

namespace Ii.Tests;

/// <summary>
/// E-signal Part 5 Step 4 — proves the interpretation catalogue is well-formed and the prompt
/// generation is deterministic, WITHOUT running any LLM call (that is Step 5). Belief-side: a new,
/// isolated <see cref="EmailInterpretationPrompt"/> class, catalogue-driven from the SAME
/// claim_key_catalogue.saas.v1.json documents use — no new claim keys. Signal-side: no new code at
/// all — <see cref="BeliefExtractionPrompt.BuildMetadataGroupSystem"/> (E1's existing metadata-group
/// builder) is reused directly for the "relationship_signals" group.
/// </summary>
public sealed class EmailInterpretationPromptTests
{
    // The five existing claim keys declared for the "email" doc type in
    // extraction_schemas.saas.v1.json — every one already has a document-path prompt_fragment;
    // email is just another (weaker) source for the SAME keys, never new ones.
    private static readonly string[] EmailBeliefKeys =
        ["payment_terms", "renewal_date", "annual_value", "invoice_amount", "sla_uptime"];

    // The five relationship-intelligence signal types with no claim-key equivalent, declared as
    // the "relationship_signals" metadata_field_group for the "email" doc type. Exactly 5 — E1's
    // proven safe group size (a 5-field group hit 100% recall; 8 hit 38%).
    private static readonly string[] SignalTypes =
        ["sentiment", "commitment", "issue_raised", "stakeholder_signal", "request"];

    [Fact]
    public void EmailSchema_IsRegistered_WithExpectedBeliefKeysAndSignalGroup()
    {
        var profile = TestHelpers.LoadProfile();

        Assert.True(profile.ExtractionSchemas.TryGetValue("email", out var schema));
        Assert.Equal(EmailBeliefKeys, schema!.BeliefKeys);

        var group = Assert.Single(schema.MetadataFieldGroups);
        Assert.Equal("relationship_signals", group.Name);
        Assert.Equal(SignalTypes, group.Fields);

        // responsiveness is catalogued (next test) but deliberately never in a group — it's
        // computed deterministically, not LLM-interpreted (spec Appendix #2).
        Assert.DoesNotContain("responsiveness", group.Fields);
    }

    [Fact]
    public void AllSixSignalTypes_HaveCompleteCatalogueDefinitions()
    {
        var profile = TestHelpers.LoadProfile();
        var allSix = SignalTypes.Append("responsiveness");

        foreach (var type in allSix)
        {
            Assert.True(profile.MetadataFieldCatalogue.TryGetValue(type, out var def),
                $"signal type '{type}' missing from metadata_field_catalogue.saas.v1.json");
            Assert.False(string.IsNullOrWhiteSpace(def!.Definition),      $"{type}: Definition");
            Assert.False(string.IsNullOrWhiteSpace(def.PositiveExample), $"{type}: PositiveExample");
            Assert.False(string.IsNullOrWhiteSpace(def.NegativeExample), $"{type}: NegativeExample");
            Assert.False(string.IsNullOrWhiteSpace(def.PromptFragment),  $"{type}: PromptFragment");
        }
    }

    [Fact]
    public void BuildBeliefSystem_IsDeterministic()
    {
        var profile = TestHelpers.LoadProfile();

        var a = EmailInterpretationPrompt.BuildBeliefSystem(profile.ClaimKeyCatalogue, EmailBeliefKeys);
        var b = EmailInterpretationPrompt.BuildBeliefSystem(profile.ClaimKeyCatalogue, EmailBeliefKeys);

        Assert.Equal(a, b);
    }

    [Fact]
    public void BuildBeliefSystem_ContainsTheRoutingDiscipline()
    {
        // The crux of this phase: the prompt must explicitly steer casual/hypothetical/negotiating
        // mentions away from becoming beliefs (Kozmo_Phase_E_Signal_Spec.md §2.3/§7).
        var profile   = TestHelpers.LoadProfile();
        var generated = EmailInterpretationPrompt.BuildBeliefSystem(profile.ClaimKeyCatalogue, EmailBeliefKeys);

        Assert.Contains("informal", generated, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("hypothetical", generated, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("negotiat", generated, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("casual", generated, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("When in doubt, leave it out", generated);
    }

    [Fact]
    public void BuildBeliefSystem_ProjectsEveryTargetKeysFragment()
    {
        var profile   = TestHelpers.LoadProfile();
        var generated = EmailInterpretationPrompt.BuildBeliefSystem(profile.ClaimKeyCatalogue, EmailBeliefKeys);

        foreach (var key in EmailBeliefKeys)
        {
            var fragment = profile.ClaimKeyCatalogue[key].PromptFragment;
            Assert.Contains(fragment, generated);
        }
    }

    [Fact]
    public void BuildBeliefSystem_ThrowsForAKeyWithNoFragment()
    {
        // Guards the "no invented mapping" discipline: a claim key without a prompt_fragment
        // (e.g. one of the un-migrated stub keys) must fail loudly, not silently produce a
        // malformed prompt.
        var profile = TestHelpers.LoadProfile();

        Assert.Throws<InvalidOperationException>(() =>
            EmailInterpretationPrompt.BuildBeliefSystem(profile.ClaimKeyCatalogue, ["liability_cap"]));
    }

    [Fact]
    public void BuildSignalSystem_IsDeterministic()
    {
        var profile = TestHelpers.LoadProfile();

        var a = EmailInterpretationPrompt.BuildSignalSystem(profile.MetadataFieldCatalogue, SignalTypes);
        var b = EmailInterpretationPrompt.BuildSignalSystem(profile.MetadataFieldCatalogue, SignalTypes);

        Assert.Equal(a, b);
    }

    [Fact]
    public void BuildSignalSystem_IsFramedForEmail_NotAContract()
    {
        var profile   = TestHelpers.LoadProfile();
        var generated = EmailInterpretationPrompt.BuildSignalSystem(profile.MetadataFieldCatalogue, SignalTypes);

        Assert.Contains("EMAIL", generated);
        Assert.Contains("relationship", generated, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("contract analyst", generated, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildSignalSystem_ProjectsEverySignalTypesFragment()
    {
        var profile   = TestHelpers.LoadProfile();
        var generated = EmailInterpretationPrompt.BuildSignalSystem(profile.MetadataFieldCatalogue, SignalTypes);

        foreach (var type in SignalTypes)
        {
            var fragment = profile.MetadataFieldCatalogue[type].PromptFragment;
            Assert.Contains(fragment, generated);
        }

        // responsiveness must never leak into a generated prompt even though it's catalogued.
        Assert.DoesNotContain("NOT LLM-extracted", generated);
    }

    [Fact]
    public void Responsiveness_IsExcludedByConfig_NotByAMissingFragment()
    {
        // responsiveness IS fully catalogued, including a prompt_fragment (for catalogue
        // completeness/documentation) — so BuildSignalSystem would mechanically accept it if
        // asked. The real exclusion is structural: no extraction_schemas.saas.v1.json group
        // references it (EmailSchema_IsRegistered... asserts this), keeping it out of every
        // generated prompt by config, not by a code-level special case. Documented here so a
        // future catalogue edit can't accidentally wire it in without noticing.
        var profile   = TestHelpers.LoadProfile();
        var generated = EmailInterpretationPrompt.BuildSignalSystem(
            profile.MetadataFieldCatalogue, ["responsiveness"]);
        Assert.Contains("NOT LLM-extracted", generated);
    }

    [Fact]
    public void User_WrapsEmailBody_NotDocumentText()
    {
        var wrapped = EmailInterpretationPrompt.User("hello");
        Assert.StartsWith("Email body:", wrapped);
    }
}
