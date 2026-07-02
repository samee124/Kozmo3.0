using Ii.CandidateExtraction;
using Ig.Contracts;
using Ig.Resolution;
using Km.Store;
using Kozmo.Contracts;
using Kozmo.Llm;
using Xunit;

namespace Ii.Tests;

/// <summary>
/// Phase 2 Commit 3 — end-to-end real-folder tests.
///
/// Runs the full pipeline (extraction → Stages A–F) against the cassette-recorded
/// fixture texts. Vacuously green when the cassette is absent (run Kyv.CandidateRecorder
/// first to record). These tests assert the §7 two-sided checkpoint:
///   (a) customers (Revolution Medicines, Meridian, etc.) are NOT in the confirmed vendor set
///   (b) real vendors (IIVS, Aequitas, Regulus) resolve correctly
///   (c) ABC Tech / ABC Technologies both reach registry as TRIAGE (POSSIBLE_SAME_ENTITY)
/// </summary>
public sealed class EndToEndRealFolderTests : IDisposable
{
    private static readonly string RepoRoot     = FindRepoRoot();
    private static readonly string CassettePath = Path.Combine(RepoRoot, "fixtures", "kyv", "candidate-extraction.cassette.json");
    private static readonly string TextsDir     = Path.Combine(RepoRoot, "fixtures", "kyv", "texts");

    private static readonly DateTimeOffset Now =
        new DateTimeOffset(2026, 6, 30, 0, 0, 0, TimeSpan.Zero);

    private readonly SqliteEntityStore _store;
    private readonly IdentityRegistry  _registry;

    public EndToEndRealFolderTests()
    {
        _store    = new SqliteEntityStore("Data Source=:memory:");
        _registry = new IdentityRegistry(_store);
    }

    public void Dispose() => _store.Dispose();

    // ── §7 Two-sided checkpoint (a): customers NOT in vendor registry ──────────

    [Fact]
    public async Task RealFolder_RevolutionMedicines_IsNotAVendor()
    {
        var vendors = await RunFullPipelineAsync();
        if (vendors is null) return; // cassette not recorded yet

        Assert.DoesNotContain(vendors, v =>
            v.CanonicalName.Contains("Revolution", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task RealFolder_Meridian_IsNotAVendor()
    {
        var vendors = await RunFullPipelineAsync();
        if (vendors is null) return;

        Assert.DoesNotContain(vendors, v =>
            v.CanonicalName.Contains("Meridian", StringComparison.OrdinalIgnoreCase));
    }

    // ── §7 Two-sided checkpoint (b): real vendors DO resolve ──────────────────

    [Fact]
    public async Task RealFolder_IIVS_ResolvesAsOneVendor()
    {
        var vendors = await RunFullPipelineAsync();
        if (vendors is null) return;

        var iivs = vendors
            .Where(v => v.CanonicalName.Contains("Vitro", StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.Single(iivs);
    }

    [Fact]
    public async Task RealFolder_Aequitas_ResolvesAsVendor()
    {
        var vendors = await RunFullPipelineAsync();
        if (vendors is null) return;

        Assert.Contains(vendors, v =>
            v.CanonicalName.Contains("Aequitas", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task RealFolder_Regulus_ResolvesAsVendor()
    {
        var vendors = await RunFullPipelineAsync();
        if (vendors is null) return;

        Assert.Contains(vendors, v =>
            v.CanonicalName.Contains("Regulus", StringComparison.OrdinalIgnoreCase));
    }

    // ── §7 Near-miss checkpoint (c): ABC pair → TRIAGE / POSSIBLE_SAME_ENTITY ─

    [Fact]
    public async Task RealFolder_ABCPair_BothReachRegistryAsTriage()
    {
        var vendors = await RunFullPipelineAsync();
        if (vendors is null) return;

        var abc = vendors
            .Where(v => v.CanonicalName.Contains("ABC", StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.Equal(2, abc.Count);
        Assert.All(abc, v => Assert.Equal(RegistryStatus.Triage, v.Status));
        Assert.All(abc, v => Assert.Contains(ResolutionFlags.PossibleSameEntity, v.Flags));
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private async Task<IReadOnlyList<CanonicalVendor>?> RunFullPipelineAsync()
    {
        if (!File.Exists(CassettePath) || !Directory.Exists(TextsDir))
            return null;

        // ── Phase 2: Extract candidates from cassette ────────────────────────
        var llm       = new CachingLlmClient(CassettePath, recordMode: false);
        var extractor = new DocumentCandidateExtractor(llm);
        var beliefs   = new List<Ig.Contracts.CandidateIdentityBelief>();

        var textFiles = Directory.GetFiles(TextsDir, "*.txt", SearchOption.AllDirectories);
        foreach (var tf in textFiles.OrderBy(f => f))
        {
            var fileName = Path.GetFileNameWithoutExtension(tf) + ".pdf";
            var tier     = DocTypeInferrer.InferTier(fileName);
            var text     = File.ReadAllText(tf);
            try
            {
                var extracted = await extractor.ExtractAsync(text, fileName, tier);
                beliefs.AddRange(extracted);
            }
            catch (LlmCacheMissException) { /* some docs may not be in the cassette yet */ }
        }

        if (beliefs.Count == 0) return null;

        // ── Phase 1: Resolve identity (Stages A–F) ────────────────────────────
        var stageB = new EntityTypeClassificationStage(new AlwaysCompanyClassifier());
        var stageC = new ClusteringStage();
        var stageD = new CollisionStage();
        var stageE = new IdentityGate();
        var stageF = new RegistryWriter(_registry);

        var normalized = beliefs.Select(Normalizer.Normalize).ToList();
        var classified = new List<ClassifiedCandidate>(normalized.Count);
        foreach (var n in normalized)
            classified.Add(await stageB.ClassifyAsync(n));

        var clusters     = stageC.Cluster(classified);
        var annotated    = stageD.Annotate(clusters);
        var dispositions = stageE.Assign(annotated);
        await stageF.WriteAsync(annotated, dispositions, Now);

        return await _registry.GetAllAsync();
    }

    // Returns Company for every LLM-fallback call; rule engine still runs first.
    private sealed class AlwaysCompanyClassifier : IEntityTypeClassifier
    {
        public Task<EntityType> ClassifyAsync(
            string effectiveName, string comparisonKey, CancellationToken ct = default)
            => Task.FromResult(EntityType.Company);
    }

    private static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(dir))
        {
            if (File.Exists(Path.Combine(dir, "Kozmo.sln"))) return dir;
            dir = Path.GetDirectoryName(dir);
        }
        throw new InvalidOperationException("Cannot locate repo root (Kozmo.sln not found).");
    }
}
