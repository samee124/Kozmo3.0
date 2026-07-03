using Ig.Contracts;
using Ig.Resolution;
using Km.Store;
using Kozmo.Contracts;
using Kozmo.Contracts.Config;
using Kozmo.Llm;
using Wc.CheckIn;

namespace Kyv.ProgramRunner;

/// <summary>
/// Runs the REAL KyvProgramRunner pipeline end-to-end (ingest → ... → persist_beliefs) against a
/// real workspace, then returns one resolved vendor's persisted beliefs with their ids remapped
/// to fixed, deterministic values.
///
/// Why the remap: VendorFileWriteService assigns Guid.NewGuid() per belief on write, and
/// AnsweringPrompt.User (Ii.Completeness) embeds belief.Id in the LLM prompt it sends — so a
/// fresh pipeline run produces a different prompt (and therefore a different cassette key) every
/// time. Remapping to fixed ids after the real run keeps the belief set — extracted from real
/// documents, identity-correlated and banded by the real production path — stable enough to
/// record once and replay deterministically in tests.
///
/// Used by both tools/Kozmo.CompletenessRecorder (one-time live recording) and
/// Kyv.ProgramRunner.Tests (the cassette-backed replay proof) so they are byte-for-byte
/// guaranteed to build the same belief set — no duplicated fixture logic to drift out of sync.
/// </summary>
public static class RealVendorBeliefFixture
{
    /// <summary>
    /// Returns null if the workspace or either cassette is unavailable, or no vendor matching
    /// <paramref name="canonicalNameContains"/> resolved from the run. Both belief.Id and
    /// belief.EntityId are remapped to fixed values — ClusteringStage assigns ClusterId via
    /// Guid.NewGuid() too, so the resolved vendor's real id also differs run to run.
    /// </summary>
    public static async Task<IReadOnlyList<Belief>?> BuildAsync(
        string            workspacePath,
        string            candidateCassettePath,
        string            beliefCassettePath,
        SaasProfile       profile,
        string            canonicalNameContains,
        Guid              fixedVendorId,
        DateTimeOffset    now,
        CancellationToken ct = default)
    {
        if (!Directory.Exists(workspacePath) ||
            !File.Exists(candidateCassettePath) ||
            !File.Exists(beliefCassettePath))
            return null;

        using var store  = new SqliteEntityStore("Data Source=:memory:");
        var registry     = new IdentityRegistry(store);
        var checkInStore = new CheckInRepository(store);
        var llm          = new CachingLlmClient(candidateCassettePath, recordMode: false);
        var beliefLlm    = new CachingLlmClient(beliefCassettePath, recordMode: false);

        var runner = new KyvProgramRunner(
            llm, new AlwaysCompanyClassifier(), registry, checkInStore,
            entityStore: store, profile: profile, beliefLlm: beliefLlm);

        await runner.RunAsync(workspacePath, now, ct);

        var all    = await registry.GetAllAsync(ct);
        var vendor = all.FirstOrDefault(
            v => v.CanonicalName.Contains(canonicalNameContains, StringComparison.OrdinalIgnoreCase));
        if (vendor is null) return null;

        var beliefs = await store.GetCurrentBeliefsAsync(vendor.VendorId, ct);

        return beliefs
            .OrderBy(b => b.Criterion,  StringComparer.OrdinalIgnoreCase)
            .ThenBy(b => b.Derivation,  StringComparer.OrdinalIgnoreCase)
            .Select((b, i) => b with { Id = FixedId(i + 1), EntityId = fixedVendorId })
            .ToList();
    }

    private static Guid FixedId(int seq) => Guid.Parse($"de110000-0000-0000-0000-{seq:D12}");
}

file sealed class AlwaysCompanyClassifier : IEntityTypeClassifier
{
    public Task<EntityType> ClassifyAsync(
        string effectiveName, string comparisonKey, CancellationToken ct = default) =>
        Task.FromResult(EntityType.Company);
}
