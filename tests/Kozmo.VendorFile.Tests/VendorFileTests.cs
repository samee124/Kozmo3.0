using System.Text.Json;
using Ii.Contracts;
using Ii.Decay;
using Ii.Index;
using Ii.Intake;
using Ii.Observation;
using Ii.Posture;
using Ii.Rubric;
using Ii.Spine;
using Km.Store;
using Kozmo.Contracts;
using Kozmo.Contracts.Config;
using Kozmo.Llm;
using Xunit;

namespace Kozmo.VendorFile.Tests;

/// <summary>
/// Phase 0 gate: types compile, configs load, INFERRED ceiling &lt; 0.60.
/// Phase 1 gate: tier-capped belief writes, supersession.
/// Phase 4 gate: RulesExtractor parses fixture, completeness detects gaps.
/// </summary>
public sealed class VendorFileTests
{
    private static readonly string CataloguePath = FindCatalogueDir();

    // ── Phase 0 gates ─────────────────────────────────────────────────────────

    [Fact, Trait("Category", "VendorFile")]
    public void Catalogue_LoadsClaimKeyCatalogue()
    {
        var profile = new Catalogue().Load(CataloguePath);
        Assert.NotEmpty(profile.ClaimKeyCatalogue);
        Assert.True(profile.ClaimKeyCatalogue.ContainsKey("sla_uptime"));
        Assert.True(profile.ClaimKeyCatalogue.ContainsKey("annual_value"));
        Assert.True(profile.ClaimKeyCatalogue.ContainsKey("renewal_date"));
        // 15, not 14: E1 Part 7 Step 3 adds invoice_amount (invoices extract this instead of
        // annual_value) — a deliberate catalogue growth, not drift.
        Assert.True(profile.ClaimKeyCatalogue.ContainsKey("invoice_amount"));
        Assert.Equal(15, profile.ClaimKeyCatalogue.Count);
    }

    [Fact, Trait("Category", "VendorFile")]
    public void Catalogue_InferredTierCeiling_BelowCriticalGate()
    {
        var profile = new Catalogue().Load(CataloguePath);
        Assert.True(profile.SourceTiers.ContainsKey("Inferred"),
            "SourceTiers must contain 'Inferred' key.");
        var inferred = profile.SourceTiers["Inferred"];
        Assert.True(inferred.Ceiling < profile.Bands.CriticalConfidenceGate,
            $"INFERRED ceiling ({inferred.Ceiling}) must be < critical confidence gate " +
            $"({profile.Bands.CriticalConfidenceGate}). " +
            "INFERRED evidence must not force Critical on its own.");
    }

    [Fact, Trait("Category", "VendorFile")]
    public void Catalogue_DocTypeTierMap_Loaded()
    {
        var profile = new Catalogue().Load(CataloguePath);
        Assert.NotEmpty(profile.DocTypeTierMap);
        Assert.True(profile.DocTypeTierMap.ContainsKey("SignedContract"));
        Assert.Equal("Primary", profile.DocTypeTierMap["SignedContract"]);
    }

    [Fact, Trait("Category", "VendorFile")]
    public void Catalogue_ExpectedBeliefSets_HasSaasVendorClass()
    {
        var profile = new Catalogue().Load(CataloguePath);
        Assert.True(profile.ExpectedBeliefSets.ContainsKey("saas_vendor"),
            "expected_belief_sets must define 'saas_vendor' class.");
        Assert.Equal(10, profile.ExpectedBeliefSets["saas_vendor"].Count);
    }

    [Fact, Trait("Category", "VendorFile")]
    public void Belief_VendorFileFields_CompileAndDefault()
    {
        var b = new Belief(
            Id:           Guid.NewGuid(),
            EntityId:     Guid.NewGuid(),
            Dimension:    Dimension.Operational,
            Criterion:    "sla_uptime",
            Value:        0.85,
            SourceTier:   SourceTier.Verified,
            Confidence:   0.80,
            Freshness:    1.0,
            Derivation:   "vendor-file:sla_uptime",
            SourceSignals: [],
            Version:      1,
            SupersededBy: null,
            CreatedAt:    new DateTimeOffset(2025, 10, 1, 0, 0, 0, TimeSpan.Zero),
            TraceId:      Guid.NewGuid())
        {
            ClaimKey     = "sla_uptime",
            ObservedAt   = new DateTimeOffset(2025, 9, 15, 0, 0, 0, TimeSpan.Zero),
            HalfLifeDays = 30,
            Provenance   = new BeliefProvenance(Guid.NewGuid(), "page:6 §7.2")
        };

        Assert.Equal("sla_uptime", b.ClaimKey);
        Assert.Equal(30, b.HalfLifeDays);
        Assert.NotNull(b.Provenance);
        Assert.Equal("page:6 §7.2", b.Provenance.Locator);
    }

    [Fact, Trait("Category", "VendorFile")]
    public void Evidence_Record_Compiles()
    {
        var ev = new Evidence(
            EvidenceId: Guid.NewGuid(),
            VendorId:   Guid.NewGuid(),
            DocType:    DocType.SignedContract,
            SourceTier: SourceTier.Primary,
            Ref:        "contracts/test.pdf",
            DocVersion: 1,
            IngestedAt: new DateTimeOffset(2025, 10, 1, 0, 0, 0, TimeSpan.Zero));

        Assert.Equal(DocType.SignedContract, ev.DocType);
        Assert.Equal(SourceTier.Primary, ev.SourceTier);
    }

    // ── Phase 1 gates ─────────────────────────────────────────────────────────

    [Fact, Trait("Category", "VendorFile")]
    public async Task WriteService_AppliesTierCeiling_PrimaryTier()
    {
        var profile = new Catalogue().Load(CataloguePath);
        var store   = new SqliteEntityStore("Data Source=:memory:", profile);
        var svc     = new VendorFileWriteService(store, profile);
        var vendorId = Guid.NewGuid();
        var evidenceId = Guid.NewGuid();

        var belief = await svc.WriteBeliefAsync(
            vendorId:            vendorId,
            claimKey:            "sla_uptime",
            dimension:           Dimension.Operational,
            criterion:           "sla_uptime",
            rawValue:            0.90,
            tier:                SourceTier.Primary,
            extractorConfidence: 1.0,
            observedAt:          new DateTimeOffset(2025, 9, 15, 0, 0, 0, TimeSpan.Zero),
            provenance:          new BeliefProvenance(evidenceId, "page:6"),
            ingestedAt:          new DateTimeOffset(2025, 10, 1, 0, 0, 0, TimeSpan.Zero));

        // Primary ceiling = 1.0; min(1.0, 1.0) = 1.0
        Assert.Equal(1.0, belief.Confidence, precision: 10);
        Assert.Equal("sla_uptime", belief.ClaimKey);
        Assert.Equal(30, belief.HalfLifeDays); // from claim_key catalogue
    }

    // ── Value convention 1(b): real derivation text survives the write path ─────

    [Fact, Trait("Category", "VendorFile")]
    public async Task WriteService_UsesCallerDerivation_WhenSupplied()
    {
        var profile   = new Catalogue().Load(CataloguePath);
        var store     = new SqliteEntityStore("Data Source=:memory:", profile);
        var svc       = new VendorFileWriteService(store, profile);
        const string realEvidence = "doc:QBR_Q32022.pdf \"4.6 out of 5.0\"";

        var belief = await svc.WriteBeliefAsync(
            vendorId:            Guid.NewGuid(),
            claimKey:            "csat",
            dimension:           Dimension.Experiential,
            criterion:           "csat",
            rawValue:            0.80,
            tier:                SourceTier.Verified,
            extractorConfidence: 0.80,
            observedAt:          new DateTimeOffset(2025, 9, 15, 0, 0, 0, TimeSpan.Zero),
            provenance:          new BeliefProvenance(Guid.NewGuid(), "field:csat"),
            ingestedAt:          new DateTimeOffset(2025, 10, 1, 0, 0, 0, TimeSpan.Zero),
            derivation:          realEvidence);

        Assert.Equal(realEvidence, belief.Derivation);
    }

    [Theory, Trait("Category", "VendorFile")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task WriteService_FallsBackToTemplate_WhenNoDerivationSupplied(string? derivation)
    {
        var profile   = new Catalogue().Load(CataloguePath);
        var store     = new SqliteEntityStore("Data Source=:memory:", profile);
        var svc       = new VendorFileWriteService(store, profile);

        var belief = await svc.WriteBeliefAsync(
            vendorId:            Guid.NewGuid(),
            claimKey:            "csat",
            dimension:           Dimension.Experiential,
            criterion:           "csat",
            rawValue:            0.80,
            tier:                SourceTier.Verified,
            extractorConfidence: 0.80,
            observedAt:          new DateTimeOffset(2025, 9, 15, 0, 0, 0, TimeSpan.Zero),
            provenance:          new BeliefProvenance(Guid.NewGuid(), "field:csat"),
            ingestedAt:          new DateTimeOffset(2025, 10, 1, 0, 0, 0, TimeSpan.Zero),
            derivation:          derivation);

        Assert.Equal("vendor-file:csat", belief.Derivation); // exact prior behavior preserved
    }

    [Fact, Trait("Category", "VendorFile")]
    public async Task WriteService_AppliesTierCeiling_InferredTier()
    {
        var profile = new Catalogue().Load(CataloguePath);
        var store   = new SqliteEntityStore("Data Source=:memory:", profile);
        var svc     = new VendorFileWriteService(store, profile);
        var vendorId = Guid.NewGuid();

        var belief = await svc.WriteBeliefAsync(
            vendorId:            vendorId,
            claimKey:            "sla_uptime",
            dimension:           Dimension.Operational,
            criterion:           "sla_uptime",
            rawValue:            0.90,
            tier:                SourceTier.Inferred,
            extractorConfidence: 1.0,
            observedAt:          new DateTimeOffset(2025, 9, 15, 0, 0, 0, TimeSpan.Zero),
            provenance:          new BeliefProvenance(Guid.NewGuid(), "field:est"),
            ingestedAt:          new DateTimeOffset(2025, 10, 1, 0, 0, 0, TimeSpan.Zero));

        // Inferred ceiling = 0.3; min(1.0, 0.3) = 0.3 — can never force Critical
        Assert.Equal(0.3, belief.Confidence, precision: 10);
    }

    [Fact, Trait("Category", "VendorFile")]
    public async Task WriteService_StructuralClaim_ZeroConfidence()
    {
        var profile = new Catalogue().Load(CataloguePath);
        var store   = new SqliteEntityStore("Data Source=:memory:", profile);
        var svc     = new VendorFileWriteService(store, profile);
        var vendorId = Guid.NewGuid();

        var belief = await svc.WriteBeliefAsync(
            vendorId:            vendorId,
            claimKey:            "annual_value",
            dimension:           Dimension.Financial,
            criterion:           "annual_value",
            rawValue:            250000,
            tier:                SourceTier.Primary,
            extractorConfidence: 1.0,
            observedAt:          new DateTimeOffset(2025, 9, 15, 0, 0, 0, TimeSpan.Zero),
            provenance:          new BeliefProvenance(Guid.NewGuid(), "page:2 §3.1"),
            ingestedAt:          new DateTimeOffset(2025, 10, 1, 0, 0, 0, TimeSpan.Zero));

        // Structural claim: DimensionWeight=0 → Confidence forced to 0 (does not feed RubricModule)
        Assert.Equal(0.0, belief.Confidence, precision: 10);
    }

    [Fact, Trait("Category", "VendorFile")]
    public async Task WriteService_Supersession_PrimarySuperseedsVerified()
    {
        var profile  = new Catalogue().Load(CataloguePath);
        var store    = new SqliteEntityStore("Data Source=:memory:", profile);
        var svc      = new VendorFileWriteService(store, profile);
        var vendorId = Guid.NewGuid();
        var dt1      = new DateTimeOffset(2025, 9, 1, 0, 0, 0, TimeSpan.Zero);
        var dt2      = new DateTimeOffset(2025, 9, 15, 0, 0, 0, TimeSpan.Zero);

        // First: write Verified belief
        var v1 = await svc.WriteBeliefAsync(
            vendorId: vendorId, claimKey: "sla_uptime",
            dimension: Dimension.Operational, criterion: "sla_uptime",
            rawValue: 0.82, tier: SourceTier.Verified, extractorConfidence: 0.9,
            observedAt: dt1, provenance: new BeliefProvenance(Guid.NewGuid(), "row:2"),
            ingestedAt: dt1);
        Assert.Equal(1, v1.Version);

        // Second: write Primary belief — should supersede Verified (rank 4 > rank 3)
        var v2 = await svc.WriteBeliefAsync(
            vendorId: vendorId, claimKey: "sla_uptime",
            dimension: Dimension.Operational, criterion: "sla_uptime",
            rawValue: 0.92, tier: SourceTier.Primary, extractorConfidence: 1.0,
            observedAt: dt2, provenance: new BeliefProvenance(Guid.NewGuid(), "page:6"),
            ingestedAt: dt2);
        Assert.Equal(2, v2.Version);

        // Current beliefs: only v2 (v1 superseded)
        var current = await store.GetCurrentBeliefsAsync(vendorId);
        Assert.DoesNotContain(current, b => b.Id == v1.Id && b.SupersededBy == null);
    }

    [Fact, Trait("Category", "VendorFile")]
    public async Task WriteService_Supersession_StrongThenWeak_BothStayCurrent_NotAContradiction()
    {
        // Deliberately asymmetric with WriteService_Supersession_PrimarySuperseedsVerified above:
        // a Primary belief already on record, followed by a WEAKER Verified belief for the same
        // slot, is NOT a correction — it's a genuine cross-source disagreement (or, for scored
        // claim keys, an independent corroborating measurement). Both must stay current so
        // ContradictionDetector.DetectCrossSource / RubricModule's fusion can see them
        // (see MetaCognitionTests T11/T12 for the full contradiction-detection assertions this
        // store-level behavior feeds). The store must not silently pick a tier winner here.
        var profile  = new Catalogue().Load(CataloguePath);
        var store    = new SqliteEntityStore("Data Source=:memory:", profile);
        var svc      = new VendorFileWriteService(store, profile);
        var vendorId = Guid.NewGuid();
        var dt1      = new DateTimeOffset(2025, 9, 1, 0, 0, 0, TimeSpan.Zero);
        var dt2      = new DateTimeOffset(2025, 9, 15, 0, 0, 0, TimeSpan.Zero);

        // First: write Primary belief
        var strong = await svc.WriteBeliefAsync(
            vendorId: vendorId, claimKey: "sla_uptime",
            dimension: Dimension.Operational, criterion: "sla_uptime",
            rawValue: 0.92, tier: SourceTier.Primary, extractorConfidence: 1.0,
            observedAt: dt1, provenance: new BeliefProvenance(Guid.NewGuid(), "page:6"),
            ingestedAt: dt1);
        Assert.Equal(1, strong.Version);
        Assert.Null(strong.SupersededBy);

        // Second: write Verified belief — weaker, arrives later. Must NOT be superseded, and
        // must NOT touch the Primary belief either.
        var weak = await svc.WriteBeliefAsync(
            vendorId: vendorId, claimKey: "sla_uptime",
            dimension: Dimension.Operational, criterion: "sla_uptime",
            rawValue: 0.82, tier: SourceTier.Verified, extractorConfidence: 0.9,
            observedAt: dt2, provenance: new BeliefProvenance(Guid.NewGuid(), "row:2"),
            ingestedAt: dt2);

        Assert.Null(weak.SupersededBy);

        var current = await store.GetCurrentBeliefsAsync(vendorId);
        Assert.Equal(2, current.Count(b => b.SupersededBy == null));
        Assert.Contains(current, b => b.Id == strong.Id && b.SupersededBy == null);
        Assert.Contains(current, b => b.Id == weak.Id && b.SupersededBy == null);
    }

    [Fact, Trait("Category", "VendorFile")]
    public async Task WriteService_Supersession_SameTierCollision_DeterministicAcrossRuns()
    {
        // Same-tier collision (two Primary documents, e.g. an MSA and a later Amendment), with
        // identical observedAt — the realistic KYV shape today, since a single run stamps every
        // belief with the same run-wide timestamp (real per-document dates are E1 work). The
        // interim tiebreak must be deterministic content ordering, never insertion/arrival order:
        // running the two writes in either order, across independent "runs" (fresh store, fresh
        // vendor id each time), must pick the same winning content both times.
        var profile = new Catalogue().Load(CataloguePath);
        var dt      = new DateTimeOffset(2025, 9, 1, 0, 0, 0, TimeSpan.Zero);

        async Task<double> RunScenario(bool msaFirst)
        {
            var store    = new SqliteEntityStore("Data Source=:memory:", profile);
            var svc      = new VendorFileWriteService(store, profile);
            var vendorId = Guid.NewGuid();

            Task<Belief> WriteMsa() => svc.WriteBeliefAsync(
                vendorId: vendorId, claimKey: "renewal_date",
                dimension: Dimension.Strategic, criterion: "renewal_date",
                rawValue: 20270101, tier: SourceTier.Primary, extractorConfidence: 1.0,
                observedAt: dt, provenance: new BeliefProvenance(Guid.NewGuid(), "MSA §12.2"),
                ingestedAt: dt, derivation: "doc:MSA.pdf \"renews 2027-01-01\"");

            Task<Belief> WriteAmendment() => svc.WriteBeliefAsync(
                vendorId: vendorId, claimKey: "renewal_date",
                dimension: Dimension.Strategic, criterion: "renewal_date",
                rawValue: 20280630, tier: SourceTier.Primary, extractorConfidence: 1.0,
                observedAt: dt, provenance: new BeliefProvenance(Guid.NewGuid(), "Amendment 2 §3"),
                ingestedAt: dt, derivation: "doc:Amendment2.pdf \"renews 2028-06-30\"");

            if (msaFirst) { await WriteMsa(); await WriteAmendment(); }
            else          { await WriteAmendment(); await WriteMsa(); }

            var current = await store.GetCurrentBeliefsAsync(vendorId);
            Assert.Single(current);
            return current[0].Value;
        }

        var winnerValueMsaFirst   = await RunScenario(msaFirst: true);
        var winnerValueAmendFirst = await RunScenario(msaFirst: false);

        // Same winning content regardless of which document was written first.
        Assert.Equal(winnerValueMsaFirst, winnerValueAmendFirst);
    }

    // ── Phase 1 store-level enforcement ──────────────────────────────────────

    [Fact, Trait("Category", "VendorFile")]
    public async Task WritePath_ClampsToTierCeiling()
    {
        var profile = new Catalogue().Load(CataloguePath);
        var store   = new SqliteEntityStore("Data Source=:memory:", profile);
        var ts      = new DateTimeOffset(2025, 10, 1, 0, 0, 0, TimeSpan.Zero);

        // Helper: write a vendor file belief directly to the store and return stored confidence
        async Task<double> StoreAndRead(SourceTier tier, double inputConf)
        {
            var entityId = Guid.NewGuid(); // isolated entity per call
            var b = new Belief(
                Id: Guid.NewGuid(), EntityId: entityId, Dimension: Dimension.Operational,
                Criterion: "sla_uptime", Value: 0.85, SourceTier: tier,
                Confidence: inputConf, Freshness: 1.0, Derivation: "vendor-file:sla_uptime",
                SourceSignals: [], Version: 1, SupersededBy: null, CreatedAt: ts,
                TraceId: Guid.NewGuid())
            { ClaimKey = "sla_uptime", ObservedAt = ts };
            await store.AppendBeliefAsync(b);
            return (await store.GetCurrentBeliefsAsync(entityId)).Single().Confidence;
        }

        // All 5 tiers — caller passes 0.9 (above ceiling for all except Primary)
        Assert.Equal(0.9, await StoreAndRead(SourceTier.Primary,    0.9), precision: 10); // min(0.9, 1.0) = 0.9
        Assert.Equal(0.8, await StoreAndRead(SourceTier.Verified,   0.9), precision: 10); // min(0.9, 0.8) = 0.8
        Assert.Equal(0.5, await StoreAndRead(SourceTier.Reported,   0.9), precision: 10); // min(0.9, 0.5) = 0.5
        Assert.Equal(0.3, await StoreAndRead(SourceTier.Inferred,   0.9), precision: 10); // min(0.9, 0.3) = 0.3
        Assert.Equal(0.2, await StoreAndRead(SourceTier.Unverified, 0.9), precision: 10); // min(0.9, 0.2) = 0.2

        // INFERRED 0.9 → 0.3 explicit: caller cannot force confidence above 0.3 for INFERRED
        Assert.Equal(0.3, await StoreAndRead(SourceTier.Inferred, 0.9), precision: 10);

        // Signal pipeline beliefs (ClaimKey="") must NOT be clamped
        {
            var entityId = Guid.NewGuid();
            var b = new Belief(
                Id: Guid.NewGuid(), EntityId: entityId, Dimension: Dimension.Operational,
                Criterion: "uptime_sla", Value: 0.1, SourceTier: SourceTier.Verified,
                Confidence: 0.95, Freshness: 1.0, Derivation: "", SourceSignals: [],
                Version: 1, SupersededBy: null, CreatedAt: ts, TraceId: Guid.NewGuid())
            { ClaimKey = "" };
            await store.AppendBeliefAsync(b);
            var conf = (await store.GetCurrentBeliefsAsync(entityId)).Single().Confidence;
            Assert.Equal(0.95, conf, precision: 10); // Verified 0.95 passes through for signal pipeline
        }
    }

    [Fact, Trait("Category", "VendorFile")]
    public async Task Supersession_PerClaimKey_Isolated()
    {
        var profile  = new Catalogue().Load(CataloguePath);
        var store    = new SqliteEntityStore("Data Source=:memory:", profile);
        var svc      = new VendorFileWriteService(store, profile);
        var vendorId = Guid.NewGuid();
        var dt1      = new DateTimeOffset(2025, 9, 1, 0, 0, 0, TimeSpan.Zero);
        var dt2      = new DateTimeOffset(2025, 9, 15, 0, 0, 0, TimeSpan.Zero);
        var prov     = new BeliefProvenance(Guid.NewGuid(), "page:1");

        // Slot A: Verified sla_uptime
        var a = await svc.WriteBeliefAsync(
            vendorId, "sla_uptime", Dimension.Operational, "sla_uptime",
            0.85, SourceTier.Verified, 0.9, dt1, prov, dt1);

        // Slot B: Verified csat_score — independent slot
        var b = await svc.WriteBeliefAsync(
            vendorId, "csat_score", Dimension.Experiential, "csat_score",
            4.2, SourceTier.Verified, 0.9, dt1, prov, dt1);

        // Supersede slot A with higher-tier Primary
        var a2 = await svc.WriteBeliefAsync(
            vendorId, "sla_uptime", Dimension.Operational, "sla_uptime",
            0.92, SourceTier.Primary, 1.0, dt2, prov, dt2);

        var current = await store.GetCurrentBeliefsAsync(vendorId);

        // a (Verified sla_uptime) must be superseded — not active
        Assert.DoesNotContain(current, x => x.Id == a.Id && x.SupersededBy == null);
        // b (Verified csat_score) must be untouched — still active
        Assert.Contains(current, x => x.Id == b.Id && x.SupersededBy == null);
        // a2 (Primary sla_uptime) must be active
        Assert.Contains(current, x => x.Id == a2.Id && x.SupersededBy == null);
        // Exactly two active beliefs: a2 + b
        Assert.Equal(2, current.Count(x => x.SupersededBy == null));
    }

    // ── Phase 4 gates ─────────────────────────────────────────────────────────

    [Fact, Trait("Category", "VendorFile")]
    public async Task RulesExtractor_EmitsClaimKeyBeliefs()
    {
        var profile    = new Catalogue().Load(CataloguePath);
        var store      = new SqliteEntityStore("Data Source=:memory:", profile);
        var svc        = new VendorFileWriteService(store, profile);
        var extractor  = new VendorFileRulesExtractor(svc, profile);
        var vendorId   = Guid.NewGuid();
        var evidenceId = Guid.NewGuid();

        var ev = new Evidence(
            EvidenceId: evidenceId, VendorId: vendorId,
            DocType:    DocType.UsageCsv, SourceTier: SourceTier.Verified,
            Ref:        "reports/usage.csv", DocVersion: 1,
            IngestedAt: new DateTimeOffset(2025, 10, 1, 0, 0, 0, TimeSpan.Zero));

        // CSV: mapped columns (usage_trend, invoice_accuracy), one unmapped column, date column
        const string csv = """
            date,usage_trend,invoice_accuracy,unknown_column
            2025-09-30,0.72,0.80,foo
            """;

        var beliefs = await extractor.ExtractAndWriteAsync(vendorId, ev, csv);

        // Two mapped columns → two beliefs; unmapped column → no belief
        Assert.Equal(2, beliefs.Count);

        var usageTrend = beliefs.Single(b => b.ClaimKey == "usage_trend");
        Assert.Equal(SourceTier.Verified, usageTrend.SourceTier);
        Assert.Equal(0.8,    usageTrend.Confidence, precision: 10); // min(0.8, 0.8) = 0.8
        Assert.Equal("row:2", usageTrend.Provenance?.Locator);       // row 2; row 1 = header
        Assert.Equal(new DateTimeOffset(2025, 9, 30, 0, 0, 0, TimeSpan.Zero),
                     usageTrend.ObservedAt);                          // from CSV date column

        var invoiceAcc = beliefs.Single(b => b.ClaimKey == "invoice_accuracy");
        Assert.Equal(SourceTier.Verified, invoiceAcc.SourceTier);
        Assert.Equal(0.8,    invoiceAcc.Confidence, precision: 10);
        Assert.Equal("row:2", invoiceAcc.Provenance?.Locator);
        Assert.Equal(new DateTimeOffset(2025, 9, 30, 0, 0, 0, TimeSpan.Zero),
                     invoiceAcc.ObservedAt);
    }

    [Fact, Trait("Category", "VendorFile")]
    public void RulesExtractor_CorrectlyParsesFixture()
    {
        var profile = new Catalogue().Load(CataloguePath);
        var extractor = new RulesExtractor(profile);

        var evidenceId = Guid.NewGuid();
        var ev = new Evidence(evidenceId, Guid.NewGuid(), DocType.UsageCsv, SourceTier.Verified,
                              "reports/test.csv", 1, new DateTimeOffset(2025, 10, 1, 0, 0, 0, TimeSpan.Zero));

        const string fixture = """
            {
              "claims": [
                { "claim_key": "usage_trend",      "raw_value": 0.72, "locator": "row:2", "extractor_confidence": 1.0 },
                { "claim_key": "invoice_accuracy",  "raw_value": 0.80, "locator": "row:5", "extractor_confidence": 1.0 }
              ]
            }
            """;

        var claims = extractor.Extract(ev, fixture, new DateTimeOffset(2025, 9, 30, 0, 0, 0, TimeSpan.Zero));

        Assert.Equal(2, claims.Count);
        Assert.Equal("usage_trend",     claims[0].ClaimKey);
        Assert.Equal(0.72,              claims[0].NormalisedValue, precision: 10);
        Assert.Equal(SourceTier.Verified, claims[0].Tier);
        Assert.Equal("row:2",           claims[0].Locator);
        Assert.Equal(evidenceId,        claims[0].EvidenceId);
    }

    [Fact, Trait("Category", "VendorFile")]
    public async Task CompletenessService_DetectsGaps_WhenScoredClaimsAbsent()
    {
        var profile  = new Catalogue().Load(CataloguePath);
        var store    = new SqliteEntityStore("Data Source=:memory:", profile);
        var svc      = new VendorFileWriteService(store, profile);
        var comp     = new CompletenessService(profile);
        var vendorId = Guid.NewGuid();
        var ts       = new DateTimeOffset(2025, 10, 1, 0, 0, 0, TimeSpan.Zero);

        // Only write structural claims (contract_on_file, annual_value, renewal_date)
        foreach (var (key, dim, val) in new[]
        {
            ("contract_on_file", Dimension.Financial, 1.0),
            ("annual_value",     Dimension.Financial, 250000.0),
            ("renewal_date",     Dimension.Financial, 1756684800.0)
        })
        {
            await svc.WriteBeliefAsync(vendorId, key, dim, key, val,
                SourceTier.Primary, 1.0, ts, new BeliefProvenance(Guid.NewGuid(), "page:1"), ts);
        }

        var current = await store.GetCurrentBeliefsAsync(vendorId);
        var result  = comp.Compute(vendorId, current);

        // saas_vendor expects 10 slots; 3 filled, 7 missing
        Assert.Equal(3, result.FilledKeys.Count);
        Assert.Equal(7, result.GapKeys.Count);
        Assert.True(result.Ratio < 0.5);
    }

    // ── Phase 2 gate: PDF lane ────────────────────────────────────────────────

    // Page texts for a minimal sample contract used by the PDF lane test.
    private static readonly IReadOnlyDictionary<int, string> SampleContractPages =
        new Dictionary<int, string>
        {
            [1] = "SAAS SERVICE AGREEMENT\n" +
                  "§7. Renewal: This Agreement renews automatically on September 1, 2026.\n" +
                  "§7.1. Notice: Either party may terminate with 60 days written notice.",
            [2] = "§8. Fees: Annual contract value USD 240000.\n" +
                  "§9. Payment: Net 30 days from invoice date.\n" +
                  "§10. Liability: Maximum liability is USD 480000."
        };

    // The fixed LLM response that the FakeInnerLlm returns (and that the cassette stores).
    private const string PdfLlmFixedResponse = """
        {
          "claims": [
            {"claim_key":"renewal_date",  "raw_value":1756684800,"confidence":0.95,"quoted_span":"This Agreement renews automatically on September 1, 2026.","page":1,"clause":"§7"},
            {"claim_key":"notice_period", "raw_value":60,        "confidence":0.95,"quoted_span":"Either party may terminate with 60 days written notice.",   "page":1,"clause":"§7.1"},
            {"claim_key":"annual_value",  "raw_value":240000,    "confidence":0.95,"quoted_span":"Annual contract value USD 240000.",                          "page":2,"clause":"§8"},
            {"claim_key":"auto_renewal",  "raw_value":1,         "confidence":0.95,"quoted_span":"This Agreement renews automatically on September 1, 2026.","page":1,"clause":"§7"},
            {"claim_key":"payment_terms", "raw_value":30,        "confidence":0.95,"quoted_span":"Net 30 days from invoice date.",                             "page":2,"clause":"§9"},
            {"claim_key":"liability_cap", "raw_value":480000,    "confidence":0.95,"quoted_span":"Maximum liability is USD 480000.",                           "page":2,"clause":"§10"}
          ]
        }
        """;

    [Fact, Trait("Category", "VendorFile")]
    public async Task PdfLane_ReplaysDeterministically()
    {
        var profile    = new Catalogue().Load(CataloguePath);
        var vendorId   = Guid.NewGuid();
        var evidenceId = Guid.NewGuid();

        var ev = new Evidence(
            EvidenceId: evidenceId, VendorId: vendorId,
            DocType:    DocType.SignedContract, SourceTier: SourceTier.Primary,
            Ref:        "contracts/sample.pdf", DocVersion: 1,
            IngestedAt: new DateTimeOffset(2025, 10, 1, 0, 0, 0, TimeSpan.Zero));

        var effectiveDate = new DateTimeOffset(2025, 9, 15, 0, 0, 0, TimeSpan.Zero);

        var tmpCassette = Path.GetTempFileName();
        try
        {
            // ── Part 1: Record — FakeInnerLlm populates the cassette ──────────
            {
                var store     = new SqliteEntityStore("Data Source=:memory:", profile);
                var svc       = new VendorFileWriteService(store, profile);
                var fakeLlm   = new FakeInnerLlm(PdfLlmFixedResponse);
                var recordLlm = new CachingLlmClient(tmpCassette, recordMode: true, inner: fakeLlm);
                var lane      = new VendorFilePdfLane(recordLlm, svc, profile);
                await lane.ExtractAndWriteAsync(vendorId, ev, SampleContractPages, effectiveDate);
            }

            // ── Part 2: Replay — CachingLlmClient reads from cassette ─────────
            var store2    = new SqliteEntityStore("Data Source=:memory:", profile);
            var svc2      = new VendorFileWriteService(store2, profile);
            var replayLlm = new CachingLlmClient(tmpCassette, recordMode: false);
            var lane2     = new VendorFilePdfLane(replayLlm, svc2, profile);
            var beliefs   = await lane2.ExtractAndWriteAsync(vendorId, ev, SampleContractPages, effectiveDate);

            // All 6 structural claims → all PRIMARY, observed_at from effectiveDate, page locators
            Assert.Equal(6, beliefs.Count);
            Assert.All(beliefs, b => Assert.Equal(SourceTier.Primary, b.SourceTier));
            Assert.All(beliefs, b => Assert.Equal(effectiveDate, b.ObservedAt));
            Assert.All(beliefs, b => Assert.NotNull(b.Provenance));
            Assert.All(beliefs, b => Assert.StartsWith("page:", b.Provenance!.Locator));

            var renewalDate  = beliefs.Single(b => b.ClaimKey == "renewal_date");
            Assert.Equal("page:1 §7",   renewalDate.Provenance!.Locator);
            Assert.Equal(1756684800.0,  renewalDate.Value, precision: 0);

            var noticePeriod = beliefs.Single(b => b.ClaimKey == "notice_period");
            Assert.Equal("page:1 §7.1", noticePeriod.Provenance!.Locator);
            Assert.Equal(60.0,          noticePeriod.Value, precision: 0);

            var annualValue  = beliefs.Single(b => b.ClaimKey == "annual_value");
            Assert.Equal("page:2 §8",   annualValue.Provenance!.Locator);
            Assert.Equal(240000.0,      annualValue.Value, precision: 0);

            // ── Part 3: Empty cassette → LlmCacheMissException (replay integrity) ──
            var emptyTmp = Path.GetTempFileName();
            try
            {
                File.WriteAllText(emptyTmp, "{}");
                var store3   = new SqliteEntityStore("Data Source=:memory:", profile);
                var svc3     = new VendorFileWriteService(store3, profile);
                var emptyLlm = new CachingLlmClient(emptyTmp, recordMode: false);
                var lane3    = new VendorFilePdfLane(emptyLlm, svc3, profile);
                await Assert.ThrowsAsync<LlmCacheMissException>(
                    () => lane3.ExtractAndWriteAsync(vendorId, ev, SampleContractPages, effectiveDate));
            }
            finally { File.Delete(emptyTmp); }
        }
        finally { File.Delete(tmpCassette); }
    }

    // ── Phase C gate: VendorFileIntake orchestration ─────────────────────────

    [Fact, Trait("Category", "VendorFile")]
    public async Task VendorIntake_AssemblesThreeLanes()
    {
        var profile  = new Catalogue().Load(CataloguePath);
        var store    = new SqliteEntityStore("Data Source=:memory:", profile);
        var svc      = new VendorFileWriteService(store, profile);
        var obs      = new ObservationModule();             // the REAL Observation classifier
        var intake   = new VendorFileIntake(svc, profile, obs);

        // Cloudwave vendorId is baked into the fixture
        var vendorId = Guid.Parse("eeeeeeee-0001-0000-0000-000000000001");

        var fixtureJson = File.ReadAllText(FindVendorFileFixture("cloudwave.evidence.json"));

        // Evidence lanes: only Primary (contract) and Verified (CSV).
        // Email evidence is handled by the signals lane, not as a pre-extracted JSON block.
        var evidenceBlocks = ParseEvidenceBlocks(fixtureJson)
            .Where(b => profile.DocTypeTierMap.TryGetValue(
                            b.evidence.DocType.ToString(), out var t) &&
                        (t == "Primary" || t == "Verified"))
            .ToList();

        // Signals lane: CRM and Email signals with rubric-valid payload values.
        //   CRM / csat_score      → ObservationModule classifies as criterion "csat_score"
        //                         → VendorFileIntake maps to §4 claim_key "csat"
        //   CRM / roadmap_fit_score → criterion "roadmap_alignment" → claim_key "roadmap_alignment"
        //   Email / renewal_intent  → criterion "renewal_intent"    → claim_key "renewal_intent"
        var emailEvidenceId = Guid.Parse("aa000001-0003-0000-0000-000000000001");
        var ts = new DateTimeOffset(2025, 10, 1, 0, 0, 0, TimeSpan.Zero);

        var emailEvidence = new Evidence(emailEvidenceId, vendorId, DocType.Email,
            SourceTier.Reported, "email/cloudwave-csat-oct2025.eml", 1, ts);

        var feedbackSignals = new (Signal, Evidence)[]
        {
            // csat_score 4.2 → rubric numeric: 4.2 ∈ [4.0, 4.5) → score 0.8
            (new Signal(Guid.NewGuid(), vendorId, Guid.NewGuid(), SourceSystem.CRM,
                "crm-csat-001", new Dictionary<string, object?> { ["csat_score"] = 4.2 },
                ts, ts, Guid.NewGuid()), emailEvidence),

            // roadmap_fit_score 0.60 → rubric numeric: 0.60 ∈ [0.60, 0.75) → score 0.8
            (new Signal(Guid.NewGuid(), vendorId, Guid.NewGuid(), SourceSystem.CRM,
                "crm-roadmap-001", new Dictionary<string, object?> { ["roadmap_fit_score"] = 0.60 },
                ts, ts, Guid.NewGuid()), emailEvidence),

            // renewal_intent "cautious" → rubric enum → score 0.75
            (new Signal(Guid.NewGuid(), vendorId, Guid.NewGuid(), SourceSystem.Email,
                "email-renewal-001", new Dictionary<string, object?> { ["renewal_intent"] = "cautious" },
                ts, ts, Guid.NewGuid()), emailEvidence),
        };

        var beliefs = await intake.IngestAsync(vendorId, evidenceBlocks, feedbackSignals);

        // 6 contract (PRIMARY) + 4 CSV (VERIFIED) + 3 signals (REPORTED) = 13
        // annual_value moved to VERIFIED (billed amount from CSV, not in contract)
        // renewal_date is ModelDerived/Inferred — excluded from Primary/Verified filter here
        Assert.Equal(13, beliefs.Count);

        // Contract lane → all PRIMARY
        var primary = beliefs.Where(b => b.SourceTier == SourceTier.Primary).ToList();
        Assert.Equal(6, primary.Count);
        Assert.Contains(primary, b => b.ClaimKey == "sla_uptime");
        Assert.Contains(primary, b => b.ClaimKey == "auto_renewal");
        Assert.Contains(primary, b => b.ClaimKey == "notice_period");
        Assert.Contains(primary, b => b.ClaimKey == "roadmap_alignment");

        // CSV lane → all VERIFIED (now 4 including annual_value)
        var verified = beliefs.Where(b => b.SourceTier == SourceTier.Verified).ToList();
        Assert.Equal(4, verified.Count);
        Assert.Contains(verified, b => b.ClaimKey == "annual_value");
        Assert.Contains(verified, b => b.ClaimKey == "usage_trend");
        Assert.Contains(verified, b => b.ClaimKey == "invoice_accuracy");
        Assert.Contains(verified, b => b.ClaimKey == "csat");

        // Signals lane → Observation produced REPORTED beliefs with §4 claim_keys
        var reported = beliefs.Where(b => b.SourceTier == SourceTier.Reported).ToList();
        Assert.Equal(3, reported.Count);
        Assert.Contains(reported, b => b.ClaimKey == "csat");           // produced by Observation
        Assert.Contains(reported, b => b.ClaimKey == "roadmap_alignment");
        Assert.Contains(reported, b => b.ClaimKey == "renewal_intent");

        // Observation produced these: csat belief provenance carries "message_ref:" prefix
        var csatBelief = reported.Single(b => b.ClaimKey == "csat");
        Assert.StartsWith("message_ref:", csatBelief.Provenance!.Locator);

        // §2 REPORTED ceiling: confidence ≤ 0.5
        Assert.All(reported, b => Assert.True(b.Confidence <= 0.5,
            $"{b.ClaimKey} confidence {b.Confidence} exceeds Reported ceiling 0.5"));

        // All 13 beliefs readable from the store
        var stored      = await store.GetCurrentBeliefsAsync(vendorId);
        var withClaimKey = stored.Where(b => !string.IsNullOrEmpty(b.ClaimKey)).ToList();
        Assert.Equal(13, withClaimKey.Count);
    }

    [Fact, Trait("Category", "VendorFile")]
    public async Task SignalLane_AdditionalWrite_DoesNotChangeObservationClassifier()
    {
        // The vendor-file signals lane is an ADDITIONAL write alongside the existing
        // signal→posture path (ClaimKey=""). Verify:
        //   (a) ObservationModule.Classify returns the same result before and after intake runs
        //       (classifier is stateless and untouched).
        //   (b) The intake write produces a belief with ClaimKey="csat".
        //   (c) A belief with ClaimKey="" (signal-pipeline style) can coexist in the store.

        var profile  = new Catalogue().Load(CataloguePath);
        var store    = new SqliteEntityStore("Data Source=:memory:", profile);
        var svc      = new VendorFileWriteService(store, profile);
        var obs      = new ObservationModule();
        var intake   = new VendorFileIntake(svc, profile, obs);

        var vendorId  = Guid.NewGuid();
        var ts        = new DateTimeOffset(2025, 10, 1, 0, 0, 0, TimeSpan.Zero);

        var signal = new Signal(
            Id:          Guid.NewGuid(),
            EntityId:    vendorId,
            CustomerId:  Guid.NewGuid(),
            SourceSystem: SourceSystem.CRM,
            ExternalId:  "crm-csat-canary",
            Payload:     new Dictionary<string, object?> { ["csat_score"] = 4.2 },
            ObservedAt:  ts,
            ReceivedAt:  ts,
            TraceId:     Guid.NewGuid());

        var evidence = new Evidence(Guid.NewGuid(), vendorId, DocType.Email,
            SourceTier.Reported, "email/canary.eml", 1, ts);

        // ── Verify the classifier before the intake run ──────────────────────
        var resultBefore = obs.Classify(signal, profile);
        Assert.NotNull(resultBefore);
        Assert.Equal("csat_score", resultBefore!.Criterion);   // original criterion, unchanged

        // ── Run the signals lane ──────────────────────────────────────────────
        await intake.IngestAsync(vendorId, [], [(signal, evidence)]);

        // ── The classifier returns the SAME result after the intake run ───────
        var resultAfter = obs.Classify(signal, profile);
        Assert.NotNull(resultAfter);
        Assert.Equal(resultBefore.Criterion, resultAfter!.Criterion);
        Assert.Equal(resultBefore.Value,     resultAfter.Value, precision: 10);
        Assert.Equal(resultBefore.SourceTier, resultAfter.SourceTier);

        // ── Vendor-file write: belief with ClaimKey="csat" (REPORTED) ─────────
        var vendorFileBeliefs = (await store.GetCurrentBeliefsAsync(vendorId))
            .Where(b => b.ClaimKey == "csat").ToList();
        Assert.Single(vendorFileBeliefs);
        Assert.Equal(SourceTier.Reported, vendorFileBeliefs[0].SourceTier);
        Assert.True(vendorFileBeliefs[0].Confidence <= 0.5);

        // ── Signal-pipeline style belief (ClaimKey="") can coexist ───────────
        // Simulate what Ii.Spine would write for the same signal (ClaimKey="" path).
        var signalPipelineBelief = new Belief(
            Id: Guid.NewGuid(), EntityId: vendorId, Dimension: Dimension.Experiential,
            Criterion: "csat_score", Value: resultBefore.Value, SourceTier: SourceTier.Verified,
            Confidence: 0.8, Freshness: 1.0, Derivation: "rule:CRM/csat_score",
            SourceSignals: [signal.Id], Version: 1, SupersededBy: null,
            CreatedAt: ts, TraceId: Guid.NewGuid())
        { ClaimKey = "" };   // signal pipeline uses empty ClaimKey
        await store.AppendBeliefAsync(signalPipelineBelief);

        var allBeliefs = await store.GetCurrentBeliefsAsync(vendorId);
        // Both coexist: the vendor-file "csat" belief AND the signal-pipeline "" belief
        Assert.Contains(allBeliefs, b => b.ClaimKey == "csat" && b.SourceTier == SourceTier.Reported);
        Assert.Contains(allBeliefs, b => b.ClaimKey == ""     && b.Criterion  == "csat_score");
    }

    // ── Phase 3 gates: VendorRecompute ────────────────────────────────────────

    [Fact, Trait("Category", "VendorFile")]
    public async Task VendorRecompute_ProducesBandedJudgement()
    {
        var profile    = new Catalogue().Load(CataloguePath);
        var store      = new SqliteEntityStore("Data Source=:memory:", profile);
        var svc        = new VendorFileWriteService(store, profile);
        var facade     = BuildFacade(store, profile, DemoClock.Fixed);
        var vendorId   = Guid.Parse("eeeeeeee-0001-0000-0000-000000000001");
        var observedAt = DemoClock.AsOf.AddDays(-1);

        // Scored beliefs — VERIFIED (ceiling=0.8), one per dimension
        // Operational dim_score = 0.20 → below 0.40 + all dim_conf ≥ 0.60 → Critical via worst-dim-floor
        await svc.WriteBeliefAsync(vendorId, "sla_uptime",        Dimension.Operational,  "sla_uptime",        0.20, SourceTier.Verified, 1.0, observedAt, new BeliefProvenance(Guid.NewGuid(), "contract:§7"),  observedAt);
        await svc.WriteBeliefAsync(vendorId, "csat",              Dimension.Experiential, "csat",              0.80, SourceTier.Verified, 1.0, observedAt, new BeliefProvenance(Guid.NewGuid(), "csv:row2"),      observedAt);
        await svc.WriteBeliefAsync(vendorId, "usage_trend",       Dimension.Financial,    "usage_trend",       0.75, SourceTier.Verified, 1.0, observedAt, new BeliefProvenance(Guid.NewGuid(), "csv:row3"),      observedAt);
        await svc.WriteBeliefAsync(vendorId, "roadmap_alignment", Dimension.Strategic,    "roadmap_alignment", 0.70, SourceTier.Verified, 1.0, observedAt, new BeliefProvenance(Guid.NewGuid(), "email:ref1"),   observedAt);

        // Structural beliefs (PRIMARY, stored confidence=0 — must not feed Rubric)
        await svc.WriteBeliefAsync(vendorId, "annual_value", Dimension.Financial, "annual_value", 250000,     SourceTier.Primary, 1.0, observedAt, new BeliefProvenance(Guid.NewGuid(), "contract:§3"), observedAt);
        await svc.WriteBeliefAsync(vendorId, "renewal_date", Dimension.Financial, "renewal_date", 1756684800, SourceTier.Primary, 1.0, observedAt, new BeliefProvenance(Guid.NewGuid(), "contract:§7"), observedAt);

        var judgement = await facade.RecomputeVendorAsync(vendorId);
        Assert.NotNull(judgement);

        // All four dimensions scored
        Assert.Equal(4, judgement.Index.DimensionScores.Count);
        Assert.True(judgement.Index.DimensionScores.ContainsKey(Dimension.Operational));
        Assert.True(judgement.Index.DimensionScores.ContainsKey(Dimension.Experiential));
        Assert.True(judgement.Index.DimensionScores.ContainsKey(Dimension.Financial));
        Assert.True(judgement.Index.DimensionScores.ContainsKey(Dimension.Strategic));

        // Composite in [0, 1]
        Assert.InRange(judgement.Index.Composite, 0.0, 1.0);

        // Band: Critical via worst-dimension floor
        // sla_uptime dim_score ≈ 0.20 < 0.40; all dim_conf ≈ 0.782 ≥ 0.60 → Critical
        Assert.Equal(Band.Critical,            judgement.Index.Band);
        Assert.Equal("worst-dimension-floor",  judgement.Index.BandDrivenBy);

        // Posture computed (stance depends on band + renewal proximity)
        Assert.NotNull(judgement.Posture);

        // MetaCognition attached: no cross-source contradictions (single write per claim_key),
        // no gaps (all four dimensions covered by scored beliefs)
        Assert.Empty(judgement.Meta.Contradictions);
        Assert.Empty(judgement.Meta.Gaps);

        // Fingerprint is pinned; any change to belief values, scores, weights, or config_version must update this.
        Assert.Equal("2edcc8c255ff9956b77436456afe9ad217cfc1117dcb536baaeaa95cfb2ad09d", judgement.Index.Fingerprint);
    }

    [Fact, Trait("Category", "VendorFile")]
    public async Task VendorRecompute_StructuralExcludedFromScoring()
    {
        var profile    = new Catalogue().Load(CataloguePath);
        var observedAt = DemoClock.AsOf.AddDays(-1);
        var vendorId   = Guid.Parse("eeeeeeee-0002-0000-0000-000000000001");

        // ── Store A: scored + structural ─────────────────────────────────────
        var storeA  = new SqliteEntityStore("Data Source=:memory:", profile);
        var svcA    = new VendorFileWriteService(storeA, profile);
        var facadeA = BuildFacade(storeA, profile, DemoClock.Fixed);

        await svcA.WriteBeliefAsync(vendorId, "sla_uptime",        Dimension.Operational,  "sla_uptime",        0.20, SourceTier.Verified, 1.0, observedAt, new BeliefProvenance(Guid.NewGuid(), "contract:§7"), observedAt);
        await svcA.WriteBeliefAsync(vendorId, "csat",              Dimension.Experiential, "csat",              0.80, SourceTier.Verified, 1.0, observedAt, new BeliefProvenance(Guid.NewGuid(), "csv:row2"),    observedAt);
        await svcA.WriteBeliefAsync(vendorId, "usage_trend",       Dimension.Financial,    "usage_trend",       0.75, SourceTier.Verified, 1.0, observedAt, new BeliefProvenance(Guid.NewGuid(), "csv:row3"),    observedAt);
        await svcA.WriteBeliefAsync(vendorId, "roadmap_alignment", Dimension.Strategic,    "roadmap_alignment", 0.70, SourceTier.Verified, 1.0, observedAt, new BeliefProvenance(Guid.NewGuid(), "email:ref1"), observedAt);

        var annualId  = (await svcA.WriteBeliefAsync(vendorId, "annual_value", Dimension.Financial, "annual_value", 250000,     SourceTier.Primary, 1.0, observedAt, new BeliefProvenance(Guid.NewGuid(), "contract:§3"), observedAt)).Id;
        var renewalId = (await svcA.WriteBeliefAsync(vendorId, "renewal_date", Dimension.Financial, "renewal_date", 1756684800, SourceTier.Primary, 1.0, observedAt, new BeliefProvenance(Guid.NewGuid(), "contract:§7"), observedAt)).Id;

        var judgeA = await facadeA.RecomputeVendorAsync(vendorId);
        Assert.NotNull(judgeA);

        // No structural belief ID must appear in any dimension's contributing IDs
        var structuralIds = new HashSet<Guid> { annualId, renewalId };
        foreach (var (_, ds) in judgeA.Index.DimensionScores)
        {
            foreach (var id in ds.ContributingBeliefIds)
                Assert.DoesNotContain(id, structuralIds);
        }

        // ── Store B: scored only (no structural) ─────────────────────────────
        var storeB  = new SqliteEntityStore("Data Source=:memory:", profile);
        var svcB    = new VendorFileWriteService(storeB, profile);
        var facadeB = BuildFacade(storeB, profile, DemoClock.Fixed);

        await svcB.WriteBeliefAsync(vendorId, "sla_uptime",        Dimension.Operational,  "sla_uptime",        0.20, SourceTier.Verified, 1.0, observedAt, new BeliefProvenance(Guid.NewGuid(), "contract:§7"), observedAt);
        await svcB.WriteBeliefAsync(vendorId, "csat",              Dimension.Experiential, "csat",              0.80, SourceTier.Verified, 1.0, observedAt, new BeliefProvenance(Guid.NewGuid(), "csv:row2"),    observedAt);
        await svcB.WriteBeliefAsync(vendorId, "usage_trend",       Dimension.Financial,    "usage_trend",       0.75, SourceTier.Verified, 1.0, observedAt, new BeliefProvenance(Guid.NewGuid(), "csv:row3"),    observedAt);
        await svcB.WriteBeliefAsync(vendorId, "roadmap_alignment", Dimension.Strategic,    "roadmap_alignment", 0.70, SourceTier.Verified, 1.0, observedAt, new BeliefProvenance(Guid.NewGuid(), "email:ref1"), observedAt);

        var judgeB = await facadeB.RecomputeVendorAsync(vendorId);
        Assert.NotNull(judgeB);

        // Removing structural beliefs leaves composite, band, and all dim scores unchanged
        Assert.Equal(judgeA.Index.Composite, judgeB.Index.Composite, precision: 10);
        Assert.Equal(judgeA.Index.Band,      judgeB.Index.Band);
        foreach (var dim in judgeA.Index.DimensionScores.Keys)
        {
            Assert.Equal(
                judgeA.Index.DimensionScores[dim].Score,
                judgeB.Index.DimensionScores[dim].Score,
                precision: 10);
        }
    }

    [Fact, Trait("Category", "VendorFile")]
    public async Task ManagementBlock_ComputesCompletenessAndFlags()
    {
        var profile    = new Catalogue().Load(CataloguePath);
        var store      = new SqliteEntityStore("Data Source=:memory:", profile);
        var svc        = new VendorFileWriteService(store, profile);
        var facade     = BuildFacade(store, profile, DemoClock.Fixed);
        var vendorId   = Guid.NewGuid();
        var observedAt = DemoClock.AsOf.AddDays(-1);
        var prov       = new BeliefProvenance(Guid.NewGuid(), "contract:§7");

        // 4 scored beliefs — sla_uptime dim_score=0.20 → Operational is weak
        await svc.WriteBeliefAsync(vendorId, "sla_uptime",        Dimension.Operational,  "sla_uptime",        0.20, SourceTier.Verified, 1.0, observedAt, prov, observedAt,
            validUntil: DemoClock.AsOf.AddDays(14));   // next due
        await svc.WriteBeliefAsync(vendorId, "csat",              Dimension.Experiential, "csat",              0.80, SourceTier.Verified, 1.0, observedAt, prov, observedAt);
        await svc.WriteBeliefAsync(vendorId, "usage_trend",       Dimension.Financial,    "usage_trend",       0.75, SourceTier.Verified, 1.0, observedAt, prov, observedAt);
        await svc.WriteBeliefAsync(vendorId, "roadmap_alignment", Dimension.Strategic,    "roadmap_alignment", 0.70, SourceTier.Verified, 1.0, observedAt, prov, observedAt);

        // 3 structural beliefs: annual_value + renewal_date + notice_period
        await svc.WriteBeliefAsync(vendorId, "annual_value",  Dimension.Financial, "annual_value",  250000,     SourceTier.Primary, 1.0, observedAt, prov, observedAt);
        await svc.WriteBeliefAsync(vendorId, "renewal_date",  Dimension.Financial, "renewal_date",  1756684800, SourceTier.Primary, 1.0, observedAt, prov, observedAt);
        await svc.WriteBeliefAsync(vendorId, "notice_period", Dimension.Financial, "notice_period", 60,         SourceTier.Primary, 1.0, observedAt, prov, observedAt);

        var judgement  = await facade.RecomputeVendorAsync(vendorId);
        Assert.NotNull(judgement);
        var management = judgement.Management;

        // Completeness: 6 / 10 filled (notice_period not in expected_belief_sets)
        Assert.Equal(6, management.FilledCount);
        Assert.Equal(10, management.ExpectedCount);
        Assert.Equal(6.0 / 10.0, management.Completeness, precision: 10);

        // Gaps: invoice_accuracy, renewal_intent, contract_on_file, payment_terms
        Assert.Equal(4, management.GapSlots.Count);
        Assert.Contains("invoice_accuracy",  management.GapSlots);
        Assert.Contains("renewal_intent",    management.GapSlots);
        Assert.Contains("contract_on_file",  management.GapSlots);
        Assert.Contains("payment_terms",     management.GapSlots);

        // Weak dimensions: only Operational (sla_uptime dim_score = 0.20 < AtRiskMin 0.40)
        Assert.Single(management.WeakDimensions);
        Assert.Contains(Dimension.Operational, management.WeakDimensions);

        // Flags: renewal_date Sep 1 2025 − 60 days notice = Jul 3 2025; no contradictions
        Assert.Equal(new DateTimeOffset(2025, 7, 3, 0, 0, 0, TimeSpan.Zero), management.Flags.RenewalDeadline);
        Assert.False(management.Flags.HasContradictions);

        // Verification state: has VERIFIED beliefs → PartiallyVerified
        Assert.Equal(VerificationState.PartiallyVerified, management.VerificationState);

        // Refresh.NextDue = sla_uptime ValidUntil = DemoClock.AsOf + 14 days
        Assert.Equal(DemoClock.AsOf.AddDays(14), management.Refresh.NextDue);
    }

    // ── Phase 4 gate: Renderer ────────────────────────────────────────────────

    [Fact, Trait("Category", "VendorFile")]
    public async Task Renderer_EmitsAllLayers()
    {
        var profile    = new Catalogue().Load(CataloguePath);
        var store      = new SqliteEntityStore("Data Source=:memory:", profile);
        var svc        = new VendorFileWriteService(store, profile);
        var facade     = BuildFacade(store, profile, DemoClock.Fixed);
        var vendorId   = Guid.Parse("eeeeeeee-0099-0000-0000-000000000001");
        var observedAt = DemoClock.AsOf.AddDays(-1);

        // Evidence record (PRIMARY SignedContract) — appended directly to the store
        var evidenceId = Guid.NewGuid();
        var ev = new Evidence(
            EvidenceId: evidenceId, VendorId: vendorId,
            DocType:    DocType.SignedContract, SourceTier: SourceTier.Primary,
            Ref:        "contracts/renderer-test.pdf", DocVersion: 1,
            IngestedAt: observedAt);
        await store.AppendEvidenceAsync(ev);

        // 4 scored beliefs — sla_uptime=0.20 keeps Operational weak → Critical band
        await svc.WriteBeliefAsync(vendorId, "sla_uptime",        Dimension.Operational,  "sla_uptime",        0.20, SourceTier.Verified, 1.0, observedAt, new BeliefProvenance(Guid.NewGuid(), "row:2"),       observedAt);
        await svc.WriteBeliefAsync(vendorId, "csat",              Dimension.Experiential, "csat",              0.80, SourceTier.Verified, 1.0, observedAt, new BeliefProvenance(Guid.NewGuid(), "row:3"),       observedAt);
        await svc.WriteBeliefAsync(vendorId, "usage_trend",       Dimension.Financial,    "usage_trend",       0.75, SourceTier.Verified, 1.0, observedAt, new BeliefProvenance(Guid.NewGuid(), "row:4"),       observedAt);
        await svc.WriteBeliefAsync(vendorId, "roadmap_alignment", Dimension.Strategic,    "roadmap_alignment", 0.70, SourceTier.Verified, 1.0, observedAt, new BeliefProvenance(Guid.NewGuid(), "row:5"),       observedAt);

        // Primary structural beliefs — renewal_date has page+span provenance (PRIMARY citation test)
        await svc.WriteBeliefAsync(vendorId, "annual_value",  Dimension.Financial, "annual_value",  250000,     SourceTier.Primary, 1.0, observedAt, new BeliefProvenance(evidenceId, "page:2 §8"),  observedAt);
        await svc.WriteBeliefAsync(vendorId, "renewal_date",  Dimension.Financial, "renewal_date",  1756684800, SourceTier.Primary, 1.0, observedAt, new BeliefProvenance(evidenceId, "page:1 §7"),  observedAt);
        await svc.WriteBeliefAsync(vendorId, "notice_period", Dimension.Financial, "notice_period", 60,         SourceTier.Primary, 1.0, observedAt, new BeliefProvenance(evidenceId, "page:1 §7.1"), observedAt);

        // Recompute to get the full VendorJudgement
        var judgement = await facade.RecomputeVendorAsync(vendorId);
        Assert.NotNull(judgement);

        // Fetch all beliefs + evidence from the store
        var activeBeliefs = await store.GetCurrentBeliefsAsync(vendorId);
        var allBeliefs    = await store.GetBeliefHistoryAsync(vendorId);
        var evidence      = await store.GetEvidenceForVendorAsync(vendorId);

        var output = VendorFileRenderer.Render(
            vendorId:     vendorId,
            vendorName:   "Renderer Test Vendor",
            asOf:         DemoClock.AsOf,
            judgement:    judgement,
            activeBeliefs: activeBeliefs,
            allBeliefs:    allBeliefs,
            evidence:      evidence);

        // ── All 9 layer headers present ──────────────────────────────────────
        Assert.Contains("## Identity",             output);
        Assert.Contains("## Judgement",            output);
        Assert.Contains("## Belief Working State", output);
        Assert.Contains("## Belief History",       output);
        Assert.Contains("## Evidence",             output);
        Assert.Contains("## Analysis",             output);
        Assert.Contains("## Management Block",     output);
        Assert.Contains("## Ledgers",              output);
        Assert.Contains("## Narrative",            output);

        // ── PRIMARY belief shows page+span citation ───────────────────────────
        Assert.Contains("page:1 §7", output);

        // ── Ledgers are present and empty ────────────────────────────────────
        Assert.Contains("No actions this phase.",  output);
        Assert.Contains("No outcomes this phase.", output);

        // ── Narrative reflects actual band and stance ─────────────────────────
        Assert.Contains(judgement.Index.Band.ToString(),    output);
        Assert.Contains(judgement.Posture.Stance.ToString(), output);
    }

    private static IIiFacade BuildFacade(SqliteEntityStore store, SaasProfile profile, IClock clock) =>
        new IiFacade(
            new ObservationModule(), new RubricModule(), new IndexModule(),
            new PostureModule(), new DecayEngine(), store, profile,
            new EntityRegistry(), clock);

    // ── Helpers ───────────────────────────────────────────────────────────────

    // FakeInnerLlm: used in record mode to populate the cassette without hitting a real API.
    private sealed class FakeInnerLlm : IKozmoLlm
    {
        private readonly string _responseJson;
        public FakeInnerLlm(string responseJson) => _responseJson = responseJson;

        public Task<LlmResult> CompleteJsonAsync(
            string system, string user, int maxTokens = 500, CancellationToken ct = default)
            => Task.FromResult(new LlmResult(
                Answer:           JsonSerializer.Deserialize<JsonElement>(_responseJson),
                Confidence:       0.95,
                ReasoningSummary: "Extracted from contract text."));
    }

    private static string FindCatalogueDir()
    {
        var dir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(dir))
        {
            var candidate = Path.Combine(dir, "catalogue", "profiles", "saas");
            if (Directory.Exists(candidate)) return candidate;
            dir = Path.GetDirectoryName(dir);
        }
        throw new InvalidOperationException(
            "Cannot locate catalogue/profiles/saas/ directory.");
    }

    private static string FindVendorFileFixture(string filename)
    {
        var dir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(dir))
        {
            var candidate = Path.Combine(dir, "fixtures", "vendor-file", filename);
            if (File.Exists(candidate)) return candidate;
            dir = Path.GetDirectoryName(dir);
        }
        throw new FileNotFoundException(
            $"Fixture not found: fixtures/vendor-file/{filename}");
    }

    // Parses cloudwave.evidence.json into named (evidence, claimsFixtureJson, observedAt) triples.
    // claimsFixtureJson wraps the per-block claims array as {"claims":[…]} for the lane readers.
    private static IReadOnlyList<(Evidence evidence, string claimsFixtureJson, DateTimeOffset observedAt)>
        ParseEvidenceBlocks(string fixtureJson)
    {
        using var doc = JsonDocument.Parse(fixtureJson);
        var results = new List<(Evidence, string, DateTimeOffset)>();

        foreach (var item in doc.RootElement.EnumerateArray())
        {
            var ev         = item.GetProperty("evidence");
            var evidenceId = Guid.Parse(ev.GetProperty("evidence_id").GetString()!);
            var vendorId   = Guid.Parse(ev.GetProperty("vendor_id").GetString()!);
            Enum.TryParse<DocType>(ev.GetProperty("doc_type").GetString(),       out var docType);
            Enum.TryParse<SourceTier>(ev.GetProperty("source_tier").GetString(), out var sourceTier);
            var docRef     = ev.GetProperty("ref").GetString()!;
            var docVersion = ev.GetProperty("doc_version").GetInt32();
            var ingestedAt = DateTimeOffset.Parse(ev.GetProperty("ingested_at").GetString()!);

            var evidence  = new Evidence(evidenceId, vendorId, docType, sourceTier,
                                         docRef, docVersion, ingestedAt);
            var observedAt = DateTimeOffset.Parse(item.GetProperty("observed_at").GetString()!);
            var claimsJson = $"{{\"claims\":{item.GetProperty("claims").GetRawText()}}}";

            results.Add((evidence, claimsJson, observedAt));
        }
        return results;
    }
}
