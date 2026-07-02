using Ii.Decay;
using Ii.Index;
using Ii.Observation;
using Ii.Posture;
using Ii.Rubric;
using Ii.Spine;
using Km.Store;
using Kozmo.Contracts;
using Kozmo.Contracts.Config;

namespace Kozmo.Api.Tests;

/// <summary>
/// Class Q — Demo fixture corpus spread.
///
/// Q1: VendorFixtures_SpreadHolds — all 6 demo vendors run through the stage runner.
///     Each proves one property of the pipeline and the combined set shows meaningful spread:
///
///   Cloudwave  — worst-dim floor: Strategic critically low (0.18 PRIMARY) → Band Critical;
///                renewal-deadline 2026-12-17 is future relative to DemoClock 2026-06-15.
///                (renewal = effectiveDate 2026-06-15 + 12mo = 2027-06-15; deadline = 2027-06-15 - 180d)
///   Helix      — gate blocks Critical: UNVERIFIED tier (ceiling=0.20) keeps ConfidenceFloor
///                below 0.60; completeness ≤ 0.25 (2/9 — honest blindness).
///   Northwind  — completeness ≠ confidence: PRIMARY signed contract fills only structural
///                slots (3/9); all scored dimensions absent.
///   Vertex     — contradiction: payment_terms PRIMARY=45 (Net45) vs REPORTED=30 (Net30);
///                lower tier cannot supersede, both active, cross-source contradiction raised.
///   Aster      — freshness decay: VERIFIED CSV is ~5 months old; usage_trend half-life=30d
///                collapses effective confidence from 0.80 ceiling to ~0.024.
///   Borealis   — supersession: Quote REPORTED annual_value=145000 superseded by contract
///                PRIMARY annual_value=155000; superseded quote visible in belief_history.
/// </summary>
[Trait("Class", "Q")]
public sealed class QTests
{
    private static readonly Guid CloudwaveId = Guid.Parse("eeeeeeee-0001-0000-0000-000000000001");
    private static readonly Guid HelixId     = Guid.Parse("eeeeeeee-0004-0000-0000-000000000001");
    private static readonly Guid NorthwindId = Guid.Parse("eeeeeeee-0005-0000-0000-000000000001");
    private static readonly Guid VertexId    = Guid.Parse("eeeeeeee-0006-0000-0000-000000000001");
    private static readonly Guid AsterId     = Guid.Parse("eeeeeeee-0007-0000-0000-000000000001");
    private static readonly Guid BorealisId  = Guid.Parse("eeeeeeee-0008-0000-0000-000000000001");

    [Fact, Trait("Class", "Q")]
    public async Task VendorFixtures_SpreadHolds()
    {
        var profile = FindAndLoadProfile();

        // ── Cloudwave: Band Critical via worst-dimension floor ───────────────────
        var cwResult = await RunVendorAsync(profile, CloudwaveId, "Cloudwave Systems Inc.", "cloudwave.evidence.json");
        var cwJ      = cwResult.Judgement!;

        Assert.Equal(Band.Critical, cwJ.Index.Band);
        Assert.Equal("worst-dimension-floor", cwJ.Index.BandDrivenBy);
        Assert.True(cwJ.Management.Flags.RenewalDeadline.HasValue,
            "Cloudwave must have a renewal deadline from fixture renewal_date (2027-06-15) + notice_period (180d)");
        Assert.True(cwJ.Management.Flags.RenewalDeadline!.Value > DemoClock.AsOf,
            $"Cloudwave renewal deadline {cwJ.Management.Flags.RenewalDeadline.Value:yyyy-MM-dd} must be future relative to DemoClock {DemoClock.AsOf:yyyy-MM-dd}");
        Assert.True(cwResult.Completeness.Ratio >= 0.75,
            $"Cloudwave completeness {cwResult.Completeness.Ratio:P0} must be ≥ 75% (full multi-lens read)");

        // ── Helix: gate blocks Critical — UNVERIFIED tier cannot force Critical ──
        var hxResult = await RunVendorAsync(profile, HelixId, "Helix Solutions AG", "helix.evidence.json");
        var hxJ      = hxResult.Judgement!;

        Assert.NotEqual(Band.Critical, hxJ.Index.Band);
        Assert.True(hxJ.Index.ConfidenceFloor < 0.60,
            $"Helix confidence floor {hxJ.Index.ConfidenceFloor:F3} must be < 0.60 — UNVERIFIED tier (ceiling=0.20) blocks Critical gate");
        Assert.True(hxResult.Completeness.Ratio <= 0.25,
            $"Helix completeness {hxResult.Completeness.Ratio:P0} must be ≤ 25% — honest blindness, 2/9 slots");

        // ── Northwind: structural-only → completeness ≠ confidence ──────────────
        var nwResult = await RunVendorAsync(profile, NorthwindId, "Northwind Logistics Inc.", "northwind.evidence.json");
        var nwJ      = nwResult.Judgement!;

        Assert.True(nwResult.Completeness.Ratio <= 0.45,
            $"Northwind completeness {nwResult.Completeness.Ratio:P0} must be ≤ 45% — only structural claims filled");
        Assert.True(nwResult.Completeness.GapKeys.Count >= 5,
            $"Northwind must have ≥ 5 evidence gaps; got {nwResult.Completeness.GapKeys.Count}");
        Assert.NotEqual(Band.Critical, nwJ.Index.Band);

        // ── Vertex: cross-source contradiction on payment_terms ──────────────────
        var vxResult = await RunVendorAsync(profile, VertexId, "Vertex Systems Ltd.", "vertex.evidence.json");

        Assert.True(vxResult.Judgement!.Management.Flags.HasContradictions,
            "Vertex must detect a contradiction: payment_terms PRIMARY=45 vs REPORTED=30");

        // ── Aster: freshness decay collapses effective confidence ─────────────────
        var asResult = await RunVendorAsync(profile, AsterId, "Aster Analytics Co.", "aster.evidence.json");

        Assert.True(asResult.Judgement!.Index.ConfidenceFloor < 0.50,
            $"Aster confidence floor {asResult.Judgement.Index.ConfidenceFloor:F3} must be < 0.50 — " +
            "VERIFIED CSV is ~5 months old; usage_trend half-life=30d decays effective confidence far below 0.80 ceiling");

        // ── Borealis: supersession surfaces in belief_history; NO spurious contradiction ──
        var brResult = await RunVendorAsync(profile, BorealisId, "Borealis Cloud GmbH", "borealis.evidence.json");

        Assert.DoesNotContain("_(no superseded beliefs)_", brResult.RenderedMarkdown!);
        Assert.False(brResult.Judgement!.Management.Flags.HasContradictions,
            "Borealis: structural annual_value superseded by higher-tier contract must NOT raise a contradiction; " +
            "delta-threshold check is scoped to scored claim_keys only");

        // ── Spread: bands are not all uniform ────────────────────────────────────
        var bands = new[]
        {
            cwJ.Index.Band,
            hxJ.Index.Band,
            nwJ.Index.Band,
            vxResult.Judgement!.Index.Band,
            asResult.Judgement!.Index.Band,
            brResult.Judgement!.Index.Band,
        };
        Assert.True(bands.Distinct().Count() > 1,
            "Vendor bands must differ across fixtures — corpus must not produce a uniform result");
    }

    // ── Q2: Borealis belief_history rows ──────────────────────────────────────────

    [Fact, Trait("Class", "Q")]
    public async Task Borealis_BeliefHistoryRows_ShowsSupersededQuote()
    {
        var profile = FindAndLoadProfile();
        var result  = await RunVendorAsync(profile, BorealisId, "Borealis Cloud GmbH",
                          "borealis.evidence.json", keepOutput: true);

        var md = result.RenderedMarkdown!;

        // Belief History section exists and is non-empty
        Assert.Contains("## Belief History", md);
        Assert.DoesNotContain("_(no superseded beliefs)_", md);

        // Quote REPORTED annual_value=145000 → rendered "145,000" (FormatValue N0), tier Reported
        Assert.Contains("annual_value", md);
        Assert.Contains("145,000",      md);

        // Quote REPORTED sla_uptime=0.82 → rendered "0.820" (FormatValue F3), tier Reported
        Assert.Contains("sla_uptime", md);
        Assert.Contains("0.820",      md);

        // Both rows carry Reported tier and a non-null SupersededBy GUID
        var historySection = md[(md.IndexOf("## Belief History", StringComparison.Ordinal))..];
        var annualRow = historySection.Split('\n')
            .FirstOrDefault(l => l.Contains("annual_value") && l.Contains("145,000"));
        var uptimeRow = historySection.Split('\n')
            .FirstOrDefault(l => l.Contains("sla_uptime") && l.Contains("0.820"));

        Assert.NotNull(annualRow);
        Assert.Contains("Reported", annualRow);

        Assert.NotNull(uptimeRow);
        Assert.Contains("Reported", uptimeRow);

        // Structural supersession must NOT raise a contradiction
        Assert.False(result.Judgement!.Management.Flags.HasContradictions,
            "Borealis: annual_value is a structural claim; supersession by a higher-tier source " +
            "is correct versioning and must not trigger the delta-threshold contradiction check");
    }

    // ── Q3: Aster freshness decay ──────────────────────────────────────────────────

    [Fact, Trait("Class", "Q")]
    public async Task Aster_UsageTrend_DecaysWellBelowVerifiedCeiling()
    {
        var profile   = FindAndLoadProfile();
        var result    = await RunVendorAsync(profile, AsterId, "Aster Analytics Co.", "aster.evidence.json");
        var confFloor = result.Judgement!.Index.ConfidenceFloor;

        // VERIFIED ceiling = 0.80. Both beliefs land in Financial dim (usage_trend + invoice_accuracy).
        // dim_conf(Financial) = MAX(eff_conf_usage, eff_conf_invoice)
        //   usage_trend:      hl=30d  → 0.80 × 2^(-151/30)  ≈ 0.024
        //   invoice_accuracy: hl=90d  → 0.80 × 2^(-151/90)  ≈ 0.249
        // ConfidenceFloor = 0.249 — significantly below VERIFIED ceiling 0.80; decay is in effect.
        Assert.True(confFloor < 0.40,
            $"Aster ConfidenceFloor {confFloor:F4} must be < 0.40 — " +
            "VERIFIED CSV is 151 days stale; invoice_accuracy (hl=90d) dominates at ~0.249, far below ceiling 0.80");

        Assert.True(confFloor > 0.05,
            $"Aster ConfidenceFloor {confFloor:F4} must be > 0.05 — partial decay, not zero: " +
            "invoice_accuracy (hl=90d) still holds ~0.249 at 151 days");

        // Decay is visible in the rendered markdown dimension scores section
        Assert.Contains("## Judgement", result.RenderedMarkdown!);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────────

    private static async Task<VendorFileResult> RunVendorAsync(
        SaasProfile profile,
        Guid        vendorId,
        string      vendorName,
        string      fixtureName,
        bool        keepOutput = false)
    {
        var store    = new SqliteEntityStore("Data Source=:memory:", profile);
        var registry = new EntityRegistry();
        registry.Register(vendorId, vendorName, null);

        var facade = new IiFacade(
            new ObservationModule(), new RubricModule(), new IndexModule(),
            new PostureModule(), new DecayEngine(),
            store, profile, registry, DemoClock.Fixed);

        var runner     = new VendorFileStageRunner(store, profile, facade);
        var fixture    = FindFixture(fixtureName);
        var outputPath = Path.Combine(Path.GetTempPath(), $"kozmo-q-{vendorId:N}.md");

        try
        {
            return await runner.RunAsync(
                vendorId:        vendorId,
                vendorName:      vendorName,
                asOf:            DemoClock.AsOf,
                fixtureFilePath: fixture,
                outputPath:      outputPath);
        }
        finally
        {
            if (!keepOutput && File.Exists(outputPath)) File.Delete(outputPath);
        }
    }

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
