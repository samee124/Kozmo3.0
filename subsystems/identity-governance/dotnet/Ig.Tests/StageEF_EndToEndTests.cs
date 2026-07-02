using Ig.Contracts;
using Ig.Resolution;
using Km.Store;
using Kozmo.Contracts;
using Xunit;

namespace Ig.Tests;

/// <summary>
/// End-to-end tests for Stage A → Stage F, verifying the six adversarial cases
/// from §7 of KYV_Phase1_IdentityResolution_Spec.md.
/// Each test runs the full composable pipeline; stages are independently callable
/// and are NOT fused into a single method.
/// The in-memory SQLite store is fresh per test (IDisposable).
/// </summary>
public sealed class StageEF_EndToEndTests : IDisposable
{
    // ── Infrastructure ─────────────────────────────────────────────────────────

    private static readonly DateTimeOffset Now =
        new DateTimeOffset(2026, 6, 30, 0, 0, 0, TimeSpan.Zero);

    // FakeEntityTypeClassifier returns Company for any LLM-fallback call.
    // PERSON and INTERNAL cases are classified by rule and never reach the LLM.
    private static readonly EntityTypeClassificationStage StageB =
        new(new FakeEntityTypeClassifier(EntityType.Company));

    private static readonly ClusteringStage StageC = new();
    private static readonly CollisionStage  StageD = new();
    private static readonly IdentityGate    StageE = new();

    private readonly SqliteEntityStore _store;
    private readonly IdentityRegistry  _registry;
    private readonly RegistryWriter    StageF;

    public StageEF_EndToEndTests()
    {
        _store   = new SqliteEntityStore("Data Source=:memory:");
        _registry = new IdentityRegistry(_store);
        StageF   = new RegistryWriter(_registry);
    }

    public void Dispose() => _store.Dispose();

    // ── Pipeline helper ────────────────────────────────────────────────────────

    private static CandidateIdentityBelief Candidate(
        string     rawName,
        SourceTier tier       = SourceTier.Verified,
        double     confidence = 0.80,
        string?    domain     = null,
        string?    taxId      = null)
        => new(
            CandidateId: Guid.NewGuid(),
            RawName:     rawName,
            SourceTier:  tier,
            Confidence:  confidence,
            Provenance:  new Provenance("doc-1", null, null),
            Signals:     domain != null || taxId != null
                             ? new CandidateSignals(domain, null, taxId, null)
                             : null,
            RoleHint:    null);

    private static CandidateIdentityBelief CandidateWithRole(
        string     rawName,
        SourceTier tier       = SourceTier.Verified,
        double     confidence = 0.80,
        string?    role       = null)
        => new(
            CandidateId: Guid.NewGuid(),
            RawName:     rawName,
            SourceTier:  tier,
            Confidence:  confidence,
            Provenance:  new Provenance("doc-1", null, null),
            Signals:     null,
            RoleHint:    role);

    private async Task<(
        IReadOnlyList<CandidateCluster>      Clusters,
        IReadOnlyList<ResolutionDisposition> Dispositions)>
        RunAtoF(params CandidateIdentityBelief[] beliefs)
    {
        // Stage A — Normalize
        var normalized = beliefs.Select(Normalizer.Normalize).ToList();

        // Stage B — Classify
        var classified = new List<ClassifiedCandidate>(normalized.Count);
        foreach (var n in normalized)
            classified.Add(await StageB.ClassifyAsync(n));

        // Stage C — Cluster
        var clusters = StageC.Cluster(classified);

        // Stage D — Annotate
        var annotated = StageD.Annotate(clusters);

        // Stage E — Disposition
        var dispositions = StageE.Assign(annotated);

        // Stage F — Write to Registry
        await StageF.WriteAsync(annotated, dispositions, Now);

        return (annotated, dispositions);
    }

    // ── §7 Case 1 — CloudWave ×3 → ONE vendor, 3 aliases, CONFIRMED ───────────

    [Fact]
    public async Task Identity_ThreeVariants_ResolveToOneVendor()
    {
        var (clusters, dispositions) = await RunAtoF(
            Candidate("Cloud Wave Inc.", SourceTier.Primary,  0.95),
            Candidate("CloudWave",       SourceTier.Verified, 0.80),
            Candidate("CLOUDWAVE LLC",   SourceTier.Reported, 0.50));

        Assert.Single(clusters);
        Assert.Single(dispositions);
        Assert.Equal(Disposition.AutoConfirm, dispositions[0].Disposition);
    }

    [Fact]
    public async Task Identity_ThreeVariants_RegistryHasOneVendorWithThreeAliases()
    {
        await RunAtoF(
            Candidate("Cloud Wave Inc.", SourceTier.Primary,  0.95),
            Candidate("CloudWave",       SourceTier.Verified, 0.80),
            Candidate("CLOUDWAVE LLC",   SourceTier.Reported, 0.50));

        var all = await _registry.GetAllAsync();

        Assert.Single(all);
        var vendor = all[0];
        Assert.Equal("Cloud Wave Inc.",     vendor.CanonicalName);
        Assert.Equal(RegistryStatus.Confirmed, vendor.Status);
        Assert.Equal(3,                     vendor.Aliases.Count);
        Assert.Contains(vendor.Aliases, a => a.RawName == "Cloud Wave Inc.");
        Assert.Contains(vendor.Aliases, a => a.RawName == "CloudWave");
        Assert.Contains(vendor.Aliases, a => a.RawName == "CLOUDWAVE LLC");
    }

    [Fact]
    public async Task Identity_ThreeVariants_ComparisonKeyAndEntityType()
    {
        await RunAtoF(
            Candidate("Cloud Wave Inc.", SourceTier.Primary,  0.95),
            Candidate("CloudWave",       SourceTier.Verified, 0.80),
            Candidate("CLOUDWAVE LLC",   SourceTier.Reported, 0.50));

        var vendor = (await _registry.GetAllAsync())[0];
        Assert.Equal("cloudwave",       vendor.ComparisonKey);
        Assert.Equal(EntityType.Company, vendor.EntityType);
    }

    // ── §7 Case 2 — Phoenix collision → TWO vendors, both TRIAGE, never merged ─

    [Fact]
    public async Task Identity_Collision_NotMerged_BothTriaged()
    {
        var (clusters, dispositions) = await RunAtoF(
            Candidate("Phoenix Consulting", SourceTier.Primary, 0.90, domain: "phoenix-east.com"),
            Candidate("Phoenix Consulting", SourceTier.Primary, 0.90, domain: "phoenix-west.com"));

        // HARD CHECKPOINT end-to-end: TWO clusters → TWO dispositions → TWO registry records
        Assert.Equal(2, clusters.Count);
        Assert.Equal(2, dispositions.Count);
        Assert.All(dispositions, d => Assert.Equal(Disposition.Triage, d.Disposition));
    }

    [Fact]
    public async Task Identity_Collision_TwoRegistryVendorsNeverMerged()
    {
        await RunAtoF(
            Candidate("Phoenix Consulting", SourceTier.Primary, 0.90, domain: "phoenix-east.com"),
            Candidate("Phoenix Consulting", SourceTier.Primary, 0.90, domain: "phoenix-west.com"));

        var all = await _registry.GetAllAsync();
        Assert.Equal(2, all.Count);
        Assert.All(all, v => Assert.Equal(RegistryStatus.Triage, v.Status));
    }

    [Fact]
    public async Task Identity_Collision_TriageDispositionsCarryReasonAndQuestion()
    {
        var (_, dispositions) = await RunAtoF(
            Candidate("Phoenix Consulting", SourceTier.Primary, 0.90, domain: "phoenix-east.com"),
            Candidate("Phoenix Consulting", SourceTier.Primary, 0.90, domain: "phoenix-west.com"));

        foreach (var d in dispositions)
        {
            Assert.Equal(Disposition.Triage, d.Disposition);
            Assert.NotNull(d.TriageReason);
            Assert.NotNull(d.TriageQuestion);
            // Question must name the conflicting signals so Phase 3 can ask a specific question
            Assert.True(
                d.TriageQuestion!.Contains("phoenix-east.com") ||
                d.TriageQuestion.Contains("phoenix-west.com"),
                $"triage_question should mention domain signals; got: {d.TriageQuestion}");
        }
    }

    // ── §7 Case 3 — Blackboard / Anthology → TWO vendors, SUSPECTED_REBRAND + TRIAGE ──

    [Fact]
    public async Task Identity_RebrandPair_Triaged_NotAutoLinked()
    {
        var (clusters, dispositions) = await RunAtoF(
            Candidate("Blackboard", SourceTier.Verified, 0.75, domain: "anthology.com"),
            Candidate("Anthology",  SourceTier.Verified, 0.75, domain: "anthology.com"));

        Assert.Equal(2, clusters.Count);
        Assert.All(dispositions, d => Assert.Equal(Disposition.Triage, d.Disposition));
    }

    [Fact]
    public async Task Identity_RebrandPair_TriageQuestionNamesBoth()
    {
        var (_, dispositions) = await RunAtoF(
            Candidate("Blackboard", SourceTier.Verified, 0.75, domain: "anthology.com"),
            Candidate("Anthology",  SourceTier.Verified, 0.75, domain: "anthology.com"));

        foreach (var d in dispositions)
        {
            Assert.NotNull(d.TriageReason);
            Assert.NotNull(d.TriageQuestion);
            // Phase 3 needs both names in the question to email a meaningful check-in
            Assert.Contains("Blackboard", d.TriageQuestion);
            Assert.Contains("Anthology",  d.TriageQuestion);
        }
    }

    [Fact]
    public async Task Identity_RebrandPair_TwoRegistryVendorsBothTriage()
    {
        await RunAtoF(
            Candidate("Blackboard", SourceTier.Verified, 0.75, domain: "anthology.com"),
            Candidate("Anthology",  SourceTier.Verified, 0.75, domain: "anthology.com"));

        var all = await _registry.GetAllAsync();
        Assert.Equal(2, all.Count);
        Assert.All(all, v => Assert.Equal(RegistryStatus.Triage, v.Status));
    }

    // ── §7 Case 4 — Person → dropped, never reaches Registry ──────────────────

    [Fact]
    public async Task Identity_Person_TypedAndDropped()
    {
        var (clusters, dispositions) = await RunAtoF(Candidate("John Smith"));

        Assert.Empty(clusters);
        Assert.Empty(dispositions);

        var all = await _registry.GetAllAsync();
        Assert.Empty(all);
    }

    // ── §7 Case 5 — Internal department → dropped, never reaches Registry ─────

    [Fact]
    public async Task Identity_Internal_TypedAndDropped()
    {
        var (clusters, dispositions) = await RunAtoF(Candidate("IT Procurement"));

        Assert.Empty(clusters);
        Assert.Empty(dispositions);

        var all = await _registry.GetAllAsync();
        Assert.Empty(all);
    }

    // ── §7 Case 6 — Document-title trap → Aramark, CONFIRMED ──────────────────

    [Fact]
    public async Task Identity_DocumentTitle_ResolvesToRealVendor()
    {
        // PRIMARY source (signed contract) → single clean candidate with PRIMARY → AUTO_CONFIRM.
        var (clusters, dispositions) = await RunAtoF(
            Candidate("Amendment 3 – Aramark", SourceTier.Primary, 0.95));

        Assert.Single(clusters);
        Assert.Equal("Aramark",             clusters[0].CanonicalName);
        Assert.Equal(Disposition.AutoConfirm, dispositions[0].Disposition);
    }

    [Fact]
    public async Task Identity_DocumentTitle_RegistryHasAramarkConfirmed()
    {
        await RunAtoF(Candidate("Amendment 3 – Aramark", SourceTier.Primary, 0.95));

        var all = await _registry.GetAllAsync();
        Assert.Single(all);
        var vendor = all[0];
        Assert.Equal("Aramark",                vendor.CanonicalName);
        Assert.Equal(RegistryStatus.Confirmed, vendor.Status);
        // Alias preserves the raw document name for provenance
        Assert.Contains(vendor.Aliases, a => a.RawName == "Amendment 3 – Aramark");
    }

    // ── Registry-populated test (all six together) ─────────────────────────────

    [Fact]
    public async Task Identity_RegistryPopulated_AliasesRecorded()
    {
        // Mix of all six case types in one run.
        // Expected registry entries: CloudWave (1 vendor), Phoenix (2), Blackboard/Anthology (2),
        // Aramark (1) = 6 vendors. John Smith and IT Procurement are dropped.
        await RunAtoF(
            Candidate("Cloud Wave Inc.",        SourceTier.Primary,  0.95),
            Candidate("CloudWave",              SourceTier.Verified, 0.80),
            Candidate("CLOUDWAVE LLC",          SourceTier.Reported, 0.50),
            Candidate("Phoenix Consulting",     SourceTier.Primary,  0.90, domain: "phoenix-east.com"),
            Candidate("Phoenix Consulting",     SourceTier.Primary,  0.90, domain: "phoenix-west.com"),
            Candidate("Blackboard",             SourceTier.Verified, 0.75, domain: "anthology.com"),
            Candidate("Anthology",              SourceTier.Verified, 0.75, domain: "anthology.com"),
            Candidate("John Smith"),                                                // dropped
            Candidate("IT Procurement"),                                            // dropped
            Candidate("Amendment 3 – Aramark", SourceTier.Primary,  0.95));

        var all = await _registry.GetAllAsync();

        Assert.Equal(6, all.Count);

        // CloudWave: 1 vendor, 3 aliases, Confirmed
        var cloudWave = all.Single(v => v.ComparisonKey == "cloudwave");
        Assert.Equal(3, cloudWave.Aliases.Count);
        Assert.Equal(RegistryStatus.Confirmed, cloudWave.Status);

        // Phoenix: 2 vendors, both Triage
        var phoenix = all.Where(v => v.ComparisonKey == "phoenixconsulting").ToList();
        Assert.Equal(2, phoenix.Count);
        Assert.All(phoenix, v => Assert.Equal(RegistryStatus.Triage, v.Status));

        // Blackboard + Anthology: 2 vendors, both Triage
        var rebrand = all.Where(v =>
            v.ComparisonKey == "blackboard" || v.ComparisonKey == "anthology").ToList();
        Assert.Equal(2, rebrand.Count);
        Assert.All(rebrand, v => Assert.Equal(RegistryStatus.Triage, v.Status));

        // Aramark: 1 vendor, Confirmed
        var aramark = all.Single(v => v.ComparisonKey == "aramark");
        Assert.Equal(RegistryStatus.Confirmed, aramark.Status);
        Assert.Equal("Aramark", aramark.CanonicalName);
    }

    // ── Phase 3 seam invariant — every TRIAGE has non-null reason + question ───

    [Fact]
    public async Task Every_TriageDisposition_HasNonNull_TriageReason_And_Question()
    {
        var (_, dispositions) = await RunAtoF(
            Candidate("Phoenix Consulting", SourceTier.Primary, 0.90, domain: "phoenix-east.com"),
            Candidate("Phoenix Consulting", SourceTier.Primary, 0.90, domain: "phoenix-west.com"),
            Candidate("Blackboard",         SourceTier.Verified, 0.75, domain: "anthology.com"),
            Candidate("Anthology",          SourceTier.Verified, 0.75, domain: "anthology.com"));

        var triaged = dispositions.Where(d => d.Disposition == Disposition.Triage).ToList();
        Assert.Equal(4, triaged.Count);

        foreach (var d in triaged)
        {
            Assert.False(string.IsNullOrWhiteSpace(d.TriageReason),
                $"TriageReason must be set for TRIAGE disposition of '{d.ProposedCanonicalName}'");
            Assert.False(string.IsNullOrWhiteSpace(d.TriageQuestion),
                $"TriageQuestion must be set for TRIAGE disposition of '{d.ProposedCanonicalName}'");
        }
    }

    // ── Role drop: customer/issuer/internal → NonVendor, never in registry ────

    [Fact]
    public async Task Role_CustomerDisposition_IsNonVendor()
    {
        var (_, dispositions) = await RunAtoF(
            CandidateWithRole("Revolution Medicines Corp", SourceTier.Primary, 0.95, role: "customer"));

        Assert.Single(dispositions);
        Assert.Equal(Disposition.NonVendor, dispositions[0].Disposition);
        Assert.Contains(ResolutionFlags.NonVendorEntity, dispositions[0].Flags);
    }

    [Fact]
    public async Task Role_CustomerIsNotWrittenToRegistry()
    {
        await RunAtoF(
            CandidateWithRole("Revolution Medicines Corp",        SourceTier.Primary,  0.95, role: "customer"),
            CandidateWithRole("Institute for In Vitro Sciences Inc", SourceTier.Verified, 0.80, role: "vendor"));

        var all = await _registry.GetAllAsync();

        Assert.Single(all); // Only the vendor (IIVS)
        Assert.DoesNotContain(all, v => v.CanonicalName.Contains("Revolution", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(all,      v => v.CanonicalName.Contains("Vitro",       StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Role_IssuerIsNotWrittenToRegistry()
    {
        await RunAtoF(
            CandidateWithRole("Prudential Financial Inc", SourceTier.Verified, 0.80, role: "issuer"));

        var all = await _registry.GetAllAsync();
        Assert.Empty(all);
    }

    // ── Near-miss (POSSIBLE_SAME_ENTITY) → both Triage, question names both ───

    [Fact]
    public async Task NearMiss_ABCPair_BothTriaged_WithQuestion()
    {
        var (_, dispositions) = await RunAtoF(
            CandidateWithRole("ABC Tech Inc.",        SourceTier.Verified, 0.80, role: "vendor"),
            CandidateWithRole("ABC Technologies LLC", SourceTier.Verified, 0.80, role: "vendor"));

        var triaged = dispositions.Where(d => d.Disposition == Disposition.Triage).ToList();
        Assert.Equal(2, triaged.Count);
        Assert.All(triaged, d => Assert.NotNull(d.TriageQuestion));
        Assert.All(triaged, d => Assert.Contains(ResolutionFlags.PossibleSameEntity, d.Flags));
    }

    [Fact]
    public async Task NearMiss_ABCPair_TriageQuestionNamesBothCandidates()
    {
        var (_, dispositions) = await RunAtoF(
            CandidateWithRole("ABC Tech Inc.",        SourceTier.Verified, 0.80, role: "vendor"),
            CandidateWithRole("ABC Technologies LLC", SourceTier.Verified, 0.80, role: "vendor"));

        foreach (var d in dispositions.Where(x => x.Disposition == Disposition.Triage))
        {
            Assert.True(
                d.TriageQuestion!.Contains("ABC Tech", StringComparison.OrdinalIgnoreCase) ||
                d.TriageQuestion.Contains("ABC Technologies", StringComparison.OrdinalIgnoreCase),
                $"TriageQuestion must name the ABC candidates; got: {d.TriageQuestion}");
        }
    }

    [Fact]
    public async Task NearMiss_ABCPair_BothWrittenToRegistryAsTriage()
    {
        await RunAtoF(
            CandidateWithRole("ABC Tech Inc.",        SourceTier.Verified, 0.80, role: "vendor"),
            CandidateWithRole("ABC Technologies LLC", SourceTier.Verified, 0.80, role: "vendor"));

        var all = await _registry.GetAllAsync();
        var abc = all.Where(v => v.CanonicalName.Contains("ABC", StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.Equal(2, abc.Count);
        Assert.All(abc, v => Assert.Equal(RegistryStatus.Triage, v.Status));
        Assert.All(abc, v => Assert.Contains(ResolutionFlags.PossibleSameEntity, v.Flags));
    }

    [Fact]
    public async Task NearMiss_WithCustomerPartner_VendorIsNotTriaged()
    {
        // ABC Tech is vendor, but its near-miss partner is a customer.
        // The near-miss question is moot → the vendor proceeds to AutoConfirm/Provisional.
        var (_, dispositions) = await RunAtoF(
            CandidateWithRole("ABC Tech Inc.",        SourceTier.Verified, 0.80, role: "vendor"),
            CandidateWithRole("ABC Technologies LLC", SourceTier.Verified, 0.80, role: "customer"));

        var vendor  = dispositions.Single(d => d.ComparisonKey == "abctech");
        var dropped = dispositions.Single(d => d.ComparisonKey == "abctechnologies");

        Assert.NotEqual(Disposition.Triage,     vendor.Disposition);
        Assert.Equal(Disposition.NonVendor, dropped.Disposition);
    }
}
