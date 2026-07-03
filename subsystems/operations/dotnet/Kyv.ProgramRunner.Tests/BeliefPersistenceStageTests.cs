using Ig.Contracts;
using Ii.CandidateExtraction;
using Km.Store;
using Kozmo.Contracts;
using Kozmo.Llm;
using Xunit;

namespace Kyv.ProgramRunner.Tests;

/// <summary>
/// Commit 2 — belief persistence stage. Proves the Value convention documented in
/// KYV_KNOWN_GAPS.md ("Belief bridge Commit 1 -> Commit 2") end-to-end:
///   - Scored claims (sla_uptime, csat) are banded to 0-1 via the existing proven rubric before
///     persisting — never the raw magnitude.
///   - Structural claims (payment_terms, renewal_date, annual_value) persist raw, Confidence
///     forced to 0.
///   - DocId -> VendorId correlation uses the same path RegistryWriter.Build() uses.
///
/// Cassette-backed: reuses fixtures/kyv/belief-extraction.cassette.json recorded in Commit 1
/// (no live network calls). Bypasses KyvProgramRunner's PDF ingestion — there is no PDF for the
/// hand-authored MSA fixture, only committed .txt — and instead drives DocumentBeliefExtractor
/// and BeliefPersistenceStage directly, exactly as KyvProgramRunner.RunAsync wires them.
/// </summary>
public sealed class BeliefPersistenceStageTests
{
    private const string MsaFileName = "MSA_SLA_Renewal_Signed.txt";
    private static readonly DateTimeOffset Now = new(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly string RepoRoot     = FindRepoRoot();
    private static readonly string CassettePath = Path.Combine(RepoRoot, "fixtures", "kyv", "belief-extraction.cassette.json");
    private static readonly string MsaPath      = Path.Combine(
        RepoRoot, "fixtures", "kyv", "texts", "belief-extraction", MsaFileName);

    [SkippableFact]
    public async Task MsaCandidates_PersistToResolvedVendor_ScoredBanded_StructuralRaw()
    {
        Skip.If(!File.Exists(CassettePath), "Belief-extraction cassette not recorded yet.");

        var profile = CatalogueTestHelper.LoadProfile();
        var llm     = new CachingLlmClient(CassettePath, recordMode: false);
        var text    = File.ReadAllText(MsaPath);
        var tier    = DocTypeInferrer.InferTier(MsaFileName);

        // Real extraction — same call KyvProgramRunner.RunAsync makes during Stage 3.
        var extractor  = new DocumentBeliefExtractor(llm, profile);
        var candidates = await extractor.ExtractAsync(text, MsaFileName, tier);

        var slaCandidate = candidates.Single(c => c.Criterion == "sla_uptime");
        Assert.Equal(99.9, slaCandidate.Value, precision: 6); // sanity: extractor emits the raw magnitude

        var vendorId = Guid.NewGuid();
        var cluster  = BuildResolvedCluster(vendorId, MsaFileName);
        var disposition = new ResolutionDisposition(
            ClusterId: vendorId, MemberCandidateIds: [Guid.NewGuid()],
            ProposedCanonicalName: "Northwind Cloud Services, Inc.", ComparisonKey: "northwind cloud services",
            EntityType: EntityType.Company, Disposition: Disposition.AutoConfirm, Confidence: 0.95,
            Flags: [], TriageReason: null, TriageQuestion: null);

        using var store = new SqliteEntityStore("Data Source=:memory:");
        var stage = new BeliefPersistenceStage(store, profile);

        var written = await stage.PersistAsync(
            [(MsaFileName, candidates)], [cluster], [disposition], Now);

        Assert.Equal(candidates.Count, written);

        var persisted = await store.GetCurrentBeliefsAsync(vendorId);
        var byKey = persisted.ToDictionary(b => b.Criterion, StringComparer.OrdinalIgnoreCase);

        // ── Scored: sla_uptime must be BANDED (0-1), never the raw 99.9 magnitude ──────────
        var sla = byKey["sla_uptime"];
        Assert.NotEqual(99.9, sla.Value);
        Assert.InRange(sla.Value, 0.0, 1.0);
        Assert.Equal(1.00, sla.Value, precision: 6); // 99.9% falls in the top uptime_sla bucket
        Assert.True(sla.Confidence > 0, "Scored belief must keep a non-zero confidence — it feeds RubricModule.");

        // ── Structural: payment_terms, renewal_date, annual_value persist RAW, Confidence == 0 ──
        var paymentTerms = byKey["payment_terms"];
        Assert.Equal(candidates.Single(c => c.Criterion == "payment_terms").Value, paymentTerms.Value, precision: 6);
        Assert.Equal(0.0, paymentTerms.Confidence);

        var annualValue = byKey["annual_value"];
        Assert.Equal(candidates.Single(c => c.Criterion == "annual_value").Value, annualValue.Value, precision: 6);
        Assert.Equal(0.0, annualValue.Confidence);

        var renewalDate = byKey["renewal_date"];
        Assert.Equal(candidates.Single(c => c.Criterion == "renewal_date").Value, renewalDate.Value, precision: 6);
        Assert.Equal(0.0, renewalDate.Confidence);
        Assert.Null(renewalDate.Dimension); // structural claim — never feeds Ii.Rubric

        // ── Every persisted belief actually belongs to the resolved vendor ─────────────────
        Assert.All(persisted, b => Assert.Equal(vendorId, b.EntityId));
    }

    [Fact]
    public async Task OutOfDomainScoredValue_IsSkipped_NotPersistedAsMisbucketedScore()
    {
        // A synthetic out-of-range CSAT (0-100 scale value fed into the 1.0-5.0 csat_score band)
        // must be dropped, not silently persisted at the lowest bucket's score.
        var profile  = CatalogueTestHelper.LoadProfile();
        var vendorId = Guid.NewGuid();
        const string docId = "synthetic-out-of-range.txt";

        var candidate = new BeliefCandidate(
            Dimension:  Dimension.Experiential,
            Criterion:  "csat",
            Value:      92.0, // out of the 1.0-5.0 csat_score domain
            Confidence: 0.8,
            SourceTier: SourceTier.Verified,
            Derivation: $"doc:{docId} \"CSAT: 92%\"");

        var cluster = BuildResolvedCluster(vendorId, docId);
        var disposition = new ResolutionDisposition(
            ClusterId: vendorId, MemberCandidateIds: [Guid.NewGuid()],
            ProposedCanonicalName: "Synthetic Vendor", ComparisonKey: "synthetic vendor",
            EntityType: EntityType.Company, Disposition: Disposition.AutoConfirm, Confidence: 0.95,
            Flags: [], TriageReason: null, TriageQuestion: null);

        using var store = new SqliteEntityStore("Data Source=:memory:");
        var stage = new BeliefPersistenceStage(store, profile);

        var written = await stage.PersistAsync(
            [(docId, [candidate])], [cluster], [disposition], Now);

        Assert.Equal(0, written);
        var persisted = await store.GetCurrentBeliefsAsync(vendorId);
        Assert.Empty(persisted);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static CandidateCluster BuildResolvedCluster(Guid vendorId, string docId)
    {
        var identityCandidate = new CandidateIdentityBelief(
            CandidateId: Guid.NewGuid(), RawName: "Northwind Cloud Services, Inc.",
            SourceTier: SourceTier.Primary, Confidence: 0.9,
            Provenance: new Provenance(DocId: docId, Page: null, Span: null),
            Signals: null, RoleHint: "vendor");

        var normalized = new NormalizedCandidate(
            Candidate: identityCandidate,
            ComparisonKey: "northwind cloud services",
            EffectiveName: "Northwind Cloud Services, Inc.");

        var classified = new ClassifiedCandidate(
            Normalized: normalized, EntityType: EntityType.Company, IsDropped: false, DropReason: null);

        return new CandidateCluster(
            ClusterId: vendorId, Members: [classified],
            CanonicalName: "Northwind Cloud Services, Inc.", ComparisonKey: "northwind cloud services",
            EntityType: EntityType.Company, Confidence: 0.95, Flags: [], EntityRole: "vendor");
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
