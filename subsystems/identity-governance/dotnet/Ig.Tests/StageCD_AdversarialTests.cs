using Ig.Contracts;
using Ig.Resolution;
using Kozmo.Contracts;
using Xunit;

namespace Ig.Tests;

/// <summary>
/// Tests the six adversarial cases defined in §7 of KYV_Phase1_IdentityResolution_Spec.md.
/// Each test hand-builds CandidateIdentityBelief inputs and runs them through the full
/// pipeline: Stage A (Normalize) → Stage B (Classify) → Stage C (Cluster) → Stage D (Annotate).
/// Stage E (gate) and Stage F (Registry write) are Commit 3 and are intentionally absent.
/// </summary>
public sealed class StageCD_AdversarialTests
{
    // ── Pipeline ───────────────────────────────────────────────────────────────

    // FakeEntityTypeClassifier returns Company for any LLM-fallback call.
    // Deterministic cases (PERSON, INTERNAL, legal-suffix names) never reach the LLM.
    private static readonly EntityTypeClassificationStage _stageB =
        new(new FakeEntityTypeClassifier(EntityType.Company));

    private static readonly ClusteringStage _stageC = new();
    private static readonly CollisionStage  _stageD = new();

    private static CandidateIdentityBelief Candidate(
        string rawName,
        SourceTier tier      = SourceTier.Verified,
        double     confidence = 0.80,
        string?    domain    = null,
        string?    taxId     = null)
        => new(
            CandidateId: Guid.NewGuid(),
            RawName:     rawName,
            SourceTier:  tier,
            Confidence:  confidence,
            Provenance:  new Provenance("doc-1", null, null),
            Signals:     domain != null || taxId != null
                             ? new CandidateSignals(domain, null, taxId, null)
                             : null,
            RoleHint: null);

    private static CandidateIdentityBelief CandidateWithRole(
        string rawName,
        SourceTier tier      = SourceTier.Verified,
        double     confidence = 0.80,
        string?    role      = null)
        => new(
            CandidateId: Guid.NewGuid(),
            RawName:     rawName,
            SourceTier:  tier,
            Confidence:  confidence,
            Provenance:  new Provenance("doc-1", null, null),
            Signals:     null,
            RoleHint:    role);

    private static async Task<IReadOnlyList<CandidateCluster>> RunPipeline(
        params CandidateIdentityBelief[] beliefs)
    {
        var normalized = beliefs.Select(Normalizer.Normalize).ToList();

        var classified = new List<ClassifiedCandidate>(normalized.Count);
        foreach (var n in normalized)
            classified.Add(await _stageB.ClassifyAsync(n));

        var clusters = _stageC.Cluster(classified);
        return _stageD.Annotate(clusters);
    }

    // ── §7 Case 1 — Three CloudWave variants → ONE cluster ────────────────────

    [Fact]
    public async Task Case1_CloudWaveVariants_ProduceOneCluster()
    {
        var clusters = await RunPipeline(
            Candidate("Cloud Wave Inc.", SourceTier.Primary,  0.95),
            Candidate("CloudWave",       SourceTier.Verified, 0.80),
            Candidate("CLOUDWAVE LLC",   SourceTier.Reported, 0.50));

        Assert.Single(clusters);
        Assert.Equal(3, clusters[0].Members.Count);
        Assert.Equal("cloudwave", clusters[0].ComparisonKey);
    }

    [Fact]
    public async Task Case1_CloudWave_CanonicalNameIsLegallyCompleteForm()
    {
        var clusters = await RunPipeline(
            Candidate("Cloud Wave Inc.", SourceTier.Primary,  0.95),
            Candidate("CloudWave",       SourceTier.Verified, 0.80),
            Candidate("CLOUDWAVE LLC",   SourceTier.Reported, 0.50));

        // "Cloud Wave Inc." has a legal suffix AND the most words (3) →
        // wins over "CLOUDWAVE LLC" (suffix present, 2 words).
        Assert.Equal("Cloud Wave Inc.", clusters[0].CanonicalName);
    }

    [Fact]
    public async Task Case1_CloudWave_NoCollisionOrRebrandFlags()
    {
        var clusters = await RunPipeline(
            Candidate("Cloud Wave Inc.", SourceTier.Primary,  0.95),
            Candidate("CloudWave",       SourceTier.Verified, 0.80),
            Candidate("CLOUDWAVE LLC",   SourceTier.Reported, 0.50));

        Assert.DoesNotContain(ResolutionFlags.Collision,        clusters[0].Flags);
        Assert.DoesNotContain(ResolutionFlags.SuspectedRebrand, clusters[0].Flags);
    }

    // ── §7 Case 2 — Phoenix Consulting collision → TWO clusters, COLLISION ─────
    //
    // THE HARD CHECKPOINT (§9 / §2 Stage C): conflicting signals block a merge
    // even when comparison_keys are identical. Over-splitting is a soft fail;
    // merging two distinct vendors into one is a HARD fail.

    [Fact]
    public async Task Case2_PhoenixCollision_ProducesExactlyTwoClusters()
    {
        var clusters = await RunPipeline(
            Candidate("Phoenix Consulting", SourceTier.Primary, 0.90, domain: "phoenix-east.com"),
            Candidate("Phoenix Consulting", SourceTier.Primary, 0.90, domain: "phoenix-west.com"));

        // HARD CHECKPOINT — must be 2, never 1.
        Assert.Equal(2, clusters.Count);
    }

    [Fact]
    public async Task Case2_PhoenixCollision_BothClustersCarryCollisionFlag()
    {
        var clusters = await RunPipeline(
            Candidate("Phoenix Consulting", SourceTier.Primary, 0.90, domain: "phoenix-east.com"),
            Candidate("Phoenix Consulting", SourceTier.Primary, 0.90, domain: "phoenix-west.com"));

        Assert.Contains(ResolutionFlags.Collision, clusters[0].Flags);
        Assert.Contains(ResolutionFlags.Collision, clusters[1].Flags);
    }

    [Fact]
    public async Task Case2_PhoenixCollision_SameComparisonKeyOnBothClusters()
    {
        var clusters = await RunPipeline(
            Candidate("Phoenix Consulting", SourceTier.Primary, 0.90, domain: "phoenix-east.com"),
            Candidate("Phoenix Consulting", SourceTier.Primary, 0.90, domain: "phoenix-west.com"));

        // Both resolve to the same key — the collision is name-identical, signal-distinct.
        Assert.Equal("phoenixconsulting", clusters[0].ComparisonKey);
        Assert.Equal("phoenixconsulting", clusters[1].ComparisonKey);
    }

    // ── §7 Case 3 — Blackboard + Anthology, same domain → TWO clusters, SUSPECTED_REBRAND

    [Fact]
    public async Task Case3_BlackboardAnthology_ProducesTwoClusters()
    {
        var clusters = await RunPipeline(
            Candidate("Blackboard", SourceTier.Verified, 0.75, domain: "anthology.com"),
            Candidate("Anthology",  SourceTier.Verified, 0.75, domain: "anthology.com"));

        Assert.Equal(2, clusters.Count);
    }

    [Fact]
    public async Task Case3_BlackboardAnthology_BothFlaggedSuspectedRebrand()
    {
        var clusters = await RunPipeline(
            Candidate("Blackboard", SourceTier.Verified, 0.75, domain: "anthology.com"),
            Candidate("Anthology",  SourceTier.Verified, 0.75, domain: "anthology.com"));

        Assert.Contains(ResolutionFlags.SuspectedRebrand, clusters[0].Flags);
        Assert.Contains(ResolutionFlags.SuspectedRebrand, clusters[1].Flags);
    }

    [Fact]
    public async Task Case3_BlackboardAnthology_NotFlaggedCollision()
    {
        var clusters = await RunPipeline(
            Candidate("Blackboard", SourceTier.Verified, 0.75, domain: "anthology.com"),
            Candidate("Anthology",  SourceTier.Verified, 0.75, domain: "anthology.com"));

        // Different comparison_keys + matching signal → SUSPECTED_REBRAND, not COLLISION.
        Assert.DoesNotContain(ResolutionFlags.Collision, clusters[0].Flags);
        Assert.DoesNotContain(ResolutionFlags.Collision, clusters[1].Flags);
    }

    // ── §7 Case 4 — Person ("John Smith") → dropped, never reaches clustering ──

    [Fact]
    public async Task Case4_PersonSignatory_DroppedBeforeClustering()
    {
        var clusters = await RunPipeline(Candidate("John Smith"));

        Assert.Empty(clusters);
    }

    // ── §7 Case 5 — Internal department → dropped, never reaches clustering ────

    [Fact]
    public async Task Case5_InternalDepartment_DroppedBeforeClustering()
    {
        var clusters = await RunPipeline(Candidate("IT Procurement"));

        Assert.Empty(clusters);
    }

    // ── §7 Case 6 — Document-title trap ("Amendment 3 – Aramark") ─────────────

    [Fact]
    public async Task Case6_DocumentTitleTrap_ClustersAsRealVendor()
    {
        var clusters = await RunPipeline(Candidate("Amendment 3 – Aramark"));

        Assert.Single(clusters);
        Assert.Equal("aramark",  clusters[0].ComparisonKey);
        Assert.Equal("Aramark",  clusters[0].CanonicalName);
    }

    [Fact]
    public async Task Case6_DocumentTitleTrap_NoCollisionOrRebrandFlag()
    {
        var clusters = await RunPipeline(Candidate("Amendment 3 – Aramark"));

        Assert.DoesNotContain(ResolutionFlags.Collision,        clusters[0].Flags);
        Assert.DoesNotContain(ResolutionFlags.SuspectedRebrand, clusters[0].Flags);
    }

    // ── Near-miss: ABC Tech vs ABC Technologies (Scenario 04 / "Conflicting Information") ─

    [Fact]
    public async Task NearMiss_ABCTechVsABCTechnologies_BothFlaggedPossibleSameEntity()
    {
        // keys: "abctech" vs "abctechnologies", sim ≈ 0.467 (in [0.40, 0.75) near-miss band)
        var clusters = await RunPipeline(
            Candidate("ABC Tech Inc.",           SourceTier.Verified, 0.80),
            Candidate("ABC Technologies LLC",    SourceTier.Verified, 0.80));

        Assert.Equal(2, clusters.Count);
        Assert.All(clusters, c => Assert.Contains(ResolutionFlags.PossibleSameEntity, c.Flags));
    }

    [Fact]
    public async Task NearMiss_ABCPair_NotMerged_NoWrongMerge()
    {
        var clusters = await RunPipeline(
            Candidate("ABC Tech Inc.",        SourceTier.Verified, 0.80),
            Candidate("ABC Technologies LLC", SourceTier.Verified, 0.80));

        Assert.Equal(2, clusters.Count);
        Assert.NotEqual(clusters[0].ClusterId, clusters[1].ClusterId);
    }

    [Fact]
    public async Task NearMiss_ABCPair_NotFlaggedCollision()
    {
        var clusters = await RunPipeline(
            Candidate("ABC Tech Inc.",        SourceTier.Verified, 0.80),
            Candidate("ABC Technologies LLC", SourceTier.Verified, 0.80));

        Assert.DoesNotContain(ResolutionFlags.Collision, clusters[0].Flags);
        Assert.DoesNotContain(ResolutionFlags.Collision, clusters[1].Flags);
    }

    // ── Role aggregation on clusters ──────────────────────────────────────────

    [Fact]
    public async Task Role_CustomerBeliefs_ClusterEntityRoleIsCustomer()
    {
        var clusters = await RunPipeline(
            CandidateWithRole("Revolution Medicines Corp", SourceTier.Verified, 0.80, role: "customer"));

        Assert.Single(clusters);
        Assert.Equal("customer", clusters[0].EntityRole);
    }

    [Fact]
    public async Task Role_PrimaryCustomerBeatsVerifiedIssuer()
    {
        // Two beliefs about the same entity: Primary=customer vs Verified=issuer.
        // Highest-tier-wins: Primary (4) beats Verified (0) → customer.
        var clusters = await RunPipeline(
            CandidateWithRole("Prudential Financial Inc", SourceTier.Primary,  0.95, role: "customer"),
            CandidateWithRole("Prudential Financial",     SourceTier.Verified, 0.80, role: "issuer"));

        Assert.Single(clusters); // same key → merged
        Assert.Equal("customer", clusters[0].EntityRole);
    }

    [Fact]
    public async Task Role_NullRoleHint_EntityRoleIsUnknown()
    {
        var clusters = await RunPipeline(Candidate("Aramark"));
        Assert.Single(clusters);
        Assert.Equal("unknown", clusters[0].EntityRole);
    }

    // ── Mixed: person + internal filtered, companies proceed ──────────────────

    [Fact]
    public async Task Mixed_OnlyCompaniesReachClustering()
    {
        var clusters = await RunPipeline(
            Candidate("John Smith"),     // PERSON → dropped
            Candidate("IT Procurement"), // INTERNAL → dropped
            Candidate("Aramark"),
            Candidate("CloudWave"));

        Assert.Equal(2, clusters.Count);
        Assert.Contains(clusters, c => c.ComparisonKey == "aramark");
        Assert.Contains(clusters, c => c.ComparisonKey == "cloudwave");
    }
}
