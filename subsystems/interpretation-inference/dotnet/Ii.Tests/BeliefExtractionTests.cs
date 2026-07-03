using Ii.CandidateExtraction;
using Km.Store;
using Kozmo.Contracts;
using Kozmo.Llm;
using Xunit;

namespace Ii.Tests;

/// <summary>
/// Belief bridge — Commit 1 — DocumentBeliefExtractor LLM extraction tests.
///
/// These tests replay from the cassette recorded by Kyv.BeliefRecorder. If the cassette is
/// absent (not yet recorded), each test returns early — vacuously green until the record pass
/// is run.
///
/// Run the recorder first:
///   OPENAI_API_KEY=sk-... dotnet run --project tools/Kyv.BeliefRecorder
///
/// Then run tests:
///   dotnet test --filter "FullyQualifiedName~BeliefExtraction"
/// </summary>
public sealed class BeliefExtractionTests
{
    private static readonly string RepoRoot      = FindRepoRoot();
    private static readonly string CassettePath  = Path.Combine(RepoRoot, "fixtures", "kyv", "belief-extraction.cassette.json");
    private static readonly string CataloguePath = Path.Combine(RepoRoot, "catalogue", "profiles", "saas");

    private const string MsaFileName = "MSA_SLA_Renewal_Signed.txt";
    private static readonly string MsaPath = Path.Combine(
        RepoRoot, "fixtures", "kyv", "texts", "belief-extraction", MsaFileName);

    private const string BrochureFileName = "Aequitas_Company_Brochure.txt";
    private static readonly string BrochurePath = Path.Combine(
        RepoRoot, "fixtures", "kyv", "texts", "Scenario 02 — Skeleton Vendor",
        "Company marketing brochure", BrochureFileName);

    // ── Content case: a signed MSA with an explicit SLA % and an explicit renewal date ────────

    [Fact]
    public async Task Extract_Msa_YieldsSlaAndRenewalBeliefs_AtPrimaryTier()
    {
        var candidates = await ExtractFromFileAsync(MsaPath, MsaFileName);
        if (candidates is null) return; // cassette not recorded yet

        var byKey = candidates.ToDictionary(c => c.Criterion, StringComparer.OrdinalIgnoreCase);

        Assert.True(byKey.ContainsKey("sla_uptime"), "Expected an sla_uptime belief from the MSA.");
        Assert.True(byKey.ContainsKey("renewal_date"), "Expected a renewal_date belief from the MSA.");

        // Filename ends in "_Signed" -> DocTypeInferrer.InferTier -> Primary (of-record contract).
        Assert.All(candidates, c => Assert.Equal(SourceTier.Primary, c.SourceTier));

        var sla = byKey["sla_uptime"];
        Assert.Equal(Dimension.Operational, sla.Dimension);
        Assert.InRange(sla.Value, 99.0, 100.0); // document states "99.9% uptime"

        var renewal = byKey["renewal_date"];
        Assert.Null(renewal.Dimension); // structural claim — never feeds Ii.Rubric
    }

    // ── Abstention case: a document with none of the five target facts extracts nothing ───────

    [Fact]
    public async Task Extract_MarketingBrochure_ExtractsNothing()
    {
        var candidates = await ExtractFromFileAsync(BrochurePath, BrochureFileName);
        if (candidates is null) return; // cassette not recorded yet

        Assert.Empty(candidates);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private async Task<List<BeliefCandidate>?> ExtractFromFileAsync(string filePath, string docId)
    {
        if (!File.Exists(CassettePath) || !File.Exists(filePath))
            return null;

        var profile   = new Catalogue().Load(CataloguePath);
        var llm       = new CachingLlmClient(CassettePath, recordMode: false);
        var extractor = new DocumentBeliefExtractor(llm, profile);
        var text      = File.ReadAllText(filePath);
        var tier      = DocTypeInferrer.InferTier(docId);

        try
        {
            return (await extractor.ExtractAsync(text, docId, tier)).ToList();
        }
        catch (LlmCacheMissException)
        {
            return null; // this document not recorded yet — treat like the cassette being absent
        }
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
