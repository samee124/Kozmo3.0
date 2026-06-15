using System.Reflection;
using Kozmo.Contracts;
using Kozmo.Contracts.Interfaces;
using Ii.Observation;
using Ii.Rubric;
using Ii.Index;
using Ii.Posture;
using Ii.Decay;
using Ii.Spine;
using Km.Store;
using NetArchTest.Rules;
using Xunit;

namespace Kozmo.Architecture.Tests;

/// <summary>
/// The five CI invariant lanes. Run with:
///   dotnet test tests/Kozmo.Architecture.Tests --filter "Category=Invariant"
/// Every test must also FAIL on a deliberate violation before this gate is considered wired.
/// </summary>
public sealed class InvariantTests
{
    // Assembly anchors — type references guarantee the assemblies are in the output dir.
    private static readonly Assembly ObservationAsm = typeof(ObservationModule).Assembly;
    private static readonly Assembly RubricAsm       = typeof(RubricModule).Assembly;
    private static readonly Assembly IndexAsm        = typeof(IndexModule).Assembly;
    private static readonly Assembly PostureAsm      = typeof(PostureModule).Assembly;
    private static readonly Assembly DecayAsm        = typeof(DecayEngine).Assembly;
    private static readonly Assembly SpineAsm        = typeof(IiFacade).Assembly;
    private static readonly Assembly KmStoreAsm      = typeof(SqliteEntityStore).Assembly;

    private static readonly string CataloguePath = ArchTestHelpers.FindCatalogueDirectory();

    // ── Lane 1: Pipeline direction ────────────────────────────────────────────

    [Fact, Trait("Category", "Invariant")]
    public void Modules_do_not_reference_each_other()
    {
        var moduleNames = new[] { "Ii.Observation", "Ii.Rubric", "Ii.Index", "Ii.Posture", "Ii.Decay" };
        var modules     = new[] { ObservationAsm, RubricAsm, IndexAsm, PostureAsm, DecayAsm };

        for (var i = 0; i < modules.Length; i++)
        {
            var others = moduleNames.Where((_, j) => j != i).ToArray();
            var result = Types.InAssembly(modules[i])
                .ShouldNot().HaveDependencyOnAny(others)
                .GetResult();
            Assert.True(result.IsSuccessful,
                $"{moduleNames[i]} illegally imports: {string.Join(", ", result.FailingTypeNames ?? new List<string>())}");
        }
    }

    [Fact, Trait("Category", "Invariant")]
    public void Modules_do_not_reference_Ii_Spine()
    {
        var moduleNames = new[] { "Ii.Observation", "Ii.Rubric", "Ii.Index", "Ii.Posture", "Ii.Decay" };
        var modules     = new[] { ObservationAsm, RubricAsm, IndexAsm, PostureAsm, DecayAsm };

        for (var i = 0; i < modules.Length; i++)
        {
            var result = Types.InAssembly(modules[i])
                .ShouldNot().HaveDependencyOn("Ii.Spine")
                .GetResult();
            Assert.True(result.IsSuccessful,
                $"{moduleNames[i]} must not reference Ii.Spine (only Spine may aggregate modules).");
        }
    }

    // ── Lane 2: Belief immutability ───────────────────────────────────────────

    [Fact, Trait("Category", "Invariant")]
    public void Belief_has_no_mutable_setters()
    {
        foreach (var p in typeof(Belief).GetProperties())
        {
            var setter = p.SetMethod;
            if (setter is null) continue;
            var isInitOnly = setter.ReturnParameter
                .GetRequiredCustomModifiers()
                .Any(t => t.Name == "IsExternalInit");
            Assert.True(isInitOnly,
                $"Belief.{p.Name} has a mutable setter — all belief properties must be constructor-set or init-only.");
        }
    }

    [Fact, Trait("Category", "Invariant")]
    public void EntityStore_has_no_belief_mutation_methods()
    {
        var bannedVerbs = new[] { "Update", "Edit", "Modify", "Delete", "Remove" };
        foreach (var mi in typeof(IEntityStore).GetMethods())
        {
            var isMutation    = bannedVerbs.Any(v => mi.Name.Contains(v, StringComparison.OrdinalIgnoreCase));
            var touchesBelief = mi.Name.Contains("Belief", StringComparison.OrdinalIgnoreCase);
            Assert.False(isMutation && touchesBelief,
                $"IEntityStore.{mi.Name} looks like a belief-mutation path — the store is append-and-supersede only.");
        }
    }

    // ── Lane 4: Confidence discipline ─────────────────────────────────────────

    [Fact, Trait("Category", "Invariant")]
    public void Catalogue_ReportedTier_IsStructurallyBelowCriticalGate()
    {
        var cat      = new Catalogue().Load(CataloguePath);
        var reported = cat.SourceTiers["Reported"];
        Assert.True(reported.Weight < cat.Bands.CriticalConfidenceGate,
            $"Reported tier weight ({reported.Weight}) must be < critical confidence gate ({cat.Bands.CriticalConfidenceGate}). " +
            "One human-reported signal must never be able to force CRITICAL.");
    }

    [Fact, Trait("Category", "Invariant")]
    public void Catalogue_AllTierWeights_AreBoundedByOne()
    {
        var cat = new Catalogue().Load(CataloguePath);
        foreach (var (name, tier) in cat.SourceTiers)
            Assert.True(tier.Weight <= 1.0,
                $"Tier '{name}' weight {tier.Weight} exceeds 1.0 — confidence = tier_weight × freshness ≤ tier_weight requires weight ≤ 1.");
    }

    // ── Lane 5a: No live dependency in demo runtime ───────────────────────────

    [Fact, Trait("Category", "Invariant")]
    public void Demo_runtime_has_no_network_or_real_llm_dependency()
    {
        var runtimeNames = new[] { "Ii.Spine", "Ii.Observation", "Ii.Rubric", "Ii.Index", "Ii.Posture", "Ii.Decay", "Km.Store" };
        var runtimeAsms  = new[] { SpineAsm, ObservationAsm, RubricAsm, IndexAsm, PostureAsm, DecayAsm, KmStoreAsm };

        for (var i = 0; i < runtimeAsms.Length; i++)
        {
            var result = Types.InAssembly(runtimeAsms[i])
                .ShouldNot().HaveDependencyOnAny("Kozmo.Llm.Anthropic", "System.Net.Http")
                .GetResult();
            Assert.True(result.IsSuccessful,
                $"{runtimeNames[i]} has a forbidden live dependency: " +
                string.Join(", ", result.FailingTypeNames ?? new List<string>()));
        }
    }
}
