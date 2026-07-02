using Ii.Decay;
using Ii.Index;
using Ii.Observation;
using Ii.Posture;
using Ii.Rubric;
using Ii.Spine;
using Km.Store;
using Kozmo.Contracts.Config;

namespace Kozmo.Api.Tests;

/// <summary>
/// Class P — VendorFileStageRunner end-to-end.
///
/// P1: Given Cloudwave's evidence fixture, the stage runner produces a Vendor.md on disk
///     containing all 9 layers, a PRIMARY page-citation belief, management block, and
///     narrative that reflects the actual band/stance.
///
/// P2: Reset + replay produces the byte-identical file (deterministic pipeline).
/// </summary>
[Trait("Class", "P")]
public sealed class PTests
{
    private static readonly Guid CloudwaveId = Guid.Parse("eeeeeeee-0001-0000-0000-000000000001");

    [Fact, Trait("Class", "P")]
    public async Task StageRunner_GeneratesVendorFile()
    {
        var profile  = FindAndLoadProfile();
        var store    = new SqliteEntityStore("Data Source=:memory:", profile);
        var registry = new EntityRegistry();
        registry.Register(CloudwaveId, "Cloudwave Systems Inc.",
            new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero));

        var facade  = new IiFacade(
            new ObservationModule(), new RubricModule(), new IndexModule(),
            new PostureModule(), new DecayEngine(), store, profile, registry, DemoClock.Fixed);

        var runner      = new VendorFileStageRunner(store, profile, facade);
        var fixturePath = FindFixture("cloudwave.evidence.json");
        var outputPath  = Path.Combine(Path.GetTempPath(), $"kozmo-cloudwave-{Guid.NewGuid():N}.md");

        try
        {
            // ── P1: First run ─────────────────────────────────────────────────
            var result1 = await runner.RunAsync(
                vendorId:        CloudwaveId,
                vendorName:      "Cloudwave Systems Inc.",
                asOf:            DemoClock.AsOf,
                fixtureFilePath: fixturePath,
                outputPath:      outputPath);

            // File written to disk
            Assert.True(File.Exists(outputPath), $"Vendor.md not found at {outputPath}");
            Assert.NotNull(result1.Judgement);
            Assert.Equal(outputPath, result1.RenderedPath);

            var md1 = File.ReadAllText(outputPath);

            // All 9 layer headers present
            Assert.Contains("## Identity",             md1);
            Assert.Contains("## Judgement",            md1);
            Assert.Contains("## Belief Working State", md1);
            Assert.Contains("## Belief History",       md1);
            Assert.Contains("## Evidence",             md1);
            Assert.Contains("## Analysis",             md1);
            Assert.Contains("## Management Block",     md1);
            Assert.Contains("## Ledgers",              md1);
            Assert.Contains("## Narrative",            md1);

            // PRIMARY beliefs show page+span citations from the recorded cassette
            Assert.Contains("page:4 §8.1", md1); // auto_renewal / notice_period locator (cassette)
            Assert.Contains("page:6 §6.1", md1); // payment_terms locator (cassette)

            // Ledgers present and empty
            Assert.Contains("No actions this phase.",  md1);
            Assert.Contains("No outcomes this phase.", md1);

            // Narrative reflects actual band and stance
            Assert.Contains(result1.Judgement!.Index.Band.ToString(),     md1);
            Assert.Contains(result1.Judgement!.Posture.Stance.ToString(), md1);

            // Management block has completeness data
            Assert.Contains("Completeness", md1);

            // Evidence section lists the contract
            Assert.Contains("contracts/cloudwave-msa1.pdf", md1);

            // ── P2: Reset + replay → byte-identical file ──────────────────────
            await store.ResetAsync();

            await runner.RunAsync(
                vendorId:        CloudwaveId,
                vendorName:      "Cloudwave Systems Inc.",
                asOf:            DemoClock.AsOf,
                fixtureFilePath: fixturePath,
                outputPath:      outputPath);

            var md2 = File.ReadAllText(outputPath);
            Assert.Equal(md1, md2);
        }
        finally
        {
            if (File.Exists(outputPath)) File.Delete(outputPath);
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static SaasProfile FindAndLoadProfile()
    {
        var dir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(dir))
        {
            var candidate = Path.Combine(dir, "catalogue", "profiles", "saas");
            if (Directory.Exists(candidate)) return new Catalogue().Load(candidate);
            dir = Path.GetDirectoryName(dir);
        }
        throw new InvalidOperationException("Cannot locate catalogue/profiles/saas/");
    }

    private static string FindFixture(string filename)
    {
        var dir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(dir))
        {
            var candidate = Path.Combine(dir, "fixtures", "vendor-file", filename);
            if (File.Exists(candidate)) return candidate;
            dir = Path.GetDirectoryName(dir);
        }
        throw new FileNotFoundException($"Fixture not found: fixtures/vendor-file/{filename}");
    }
}
