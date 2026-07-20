using Kozmo.Contracts;
using Xunit;

namespace Po.VendorCall.Tests;

/// <summary>
/// Tests for TranscriptEvidenceAdapter — the bridge between TranscriptExtractionResult
/// and VendorCallEvidenceBundle that feeds enriched evidence into ReviewComposer.
/// </summary>
public sealed class TranscriptEvidenceAdapterTests
{
    private static readonly Guid   VendorId = Guid.Parse("dd000001-0000-0000-0000-000000000001");
    private static readonly DateTimeOffset Now = new(2026, 7, 16, 10, 0, 0, TimeSpan.Zero);

    // ── Empty extraction → bundle unchanged ────────────────────────────────────

    [Fact]
    public void Enrich_EmptyExtraction_ReturnsBundleUnchanged()
    {
        var bundle    = EmptyBundle();
        var extraction = EmptyExtraction();

        var result = TranscriptEvidenceAdapter.Enrich(bundle, extraction, VendorId, Now);

        Assert.Empty(result.OpenCommitments);
        Assert.Empty(result.CommercialSignals);
        Assert.Empty(result.PriorMeetingNotes);
        Assert.Empty(result.EvidenceGaps);
    }

    // ── High-confidence Commitment → OpenCommitments ───────────────────────────

    [Fact]
    public void Enrich_HighConfCommitment_AddsToOpenCommitments()
    {
        var item = MakeItem(TranscriptItemType.Commitment,
            "Vendor agreed to send SLA report by Friday",
            requiresConfirmation: false);

        var result = TranscriptEvidenceAdapter.Enrich(EmptyBundle(), ExtractionWith(item), VendorId, Now);

        Assert.Single(result.OpenCommitments);
        Assert.Empty(result.CommercialSignals);
        Assert.Empty(result.EvidenceGaps);

        var ev = result.OpenCommitments[0];
        Assert.Equal(DocType.OwnerNote, ev.DocType);
        Assert.Equal(SourceTier.Reported, ev.SourceTier);
        Assert.Equal(VendorId, ev.VendorId);
        Assert.Contains("Vendor agreed to send SLA report by Friday", ev.Ref);
    }

    // ── High-confidence NextStep → OpenCommitments ─────────────────────────────

    [Fact]
    public void Enrich_HighConfNextStep_AddsToOpenCommitments()
    {
        var item = MakeItem(TranscriptItemType.NextStep,
            "Schedule follow-up call for August",
            requiresConfirmation: false);

        var result = TranscriptEvidenceAdapter.Enrich(EmptyBundle(), ExtractionWith(item), VendorId, Now);

        Assert.Single(result.OpenCommitments);
        Assert.Equal(DocType.OwnerNote, result.OpenCommitments[0].DocType);
    }

    // ── High-confidence PricingSignal → CommercialSignals ──────────────────────

    [Fact]
    public void Enrich_HighConfPricingSignal_AddsToCommercialSignals()
    {
        var item = MakeItem(TranscriptItemType.PricingSignal,
            "Vendor indicated potential price increase next quarter",
            requiresConfirmation: false);

        var result = TranscriptEvidenceAdapter.Enrich(EmptyBundle(), ExtractionWith(item), VendorId, Now);

        Assert.Single(result.CommercialSignals);
        Assert.Empty(result.OpenCommitments);
        Assert.Equal(DocType.Communication, result.CommercialSignals[0].DocType);
    }

    // ── High-confidence ServiceSignal → CommercialSignals ──────────────────────

    [Fact]
    public void Enrich_HighConfServiceSignal_AddsToCommercialSignals()
    {
        var item = MakeItem(TranscriptItemType.ServiceSignal,
            "Platform uptime issues acknowledged by vendor",
            requiresConfirmation: false);

        var result = TranscriptEvidenceAdapter.Enrich(EmptyBundle(), ExtractionWith(item), VendorId, Now);

        Assert.Single(result.CommercialSignals);
        Assert.Equal(DocType.Communication, result.CommercialSignals[0].DocType);
    }

    // ── High-confidence Decision → PriorMeetingNotes ──────────────────────────

    [Fact]
    public void Enrich_HighConfDecision_AddsToPriorMeetingNotes()
    {
        var item = MakeItem(TranscriptItemType.Decision,
            "Both parties agreed to extend contract by one year",
            requiresConfirmation: false);

        var result = TranscriptEvidenceAdapter.Enrich(EmptyBundle(), ExtractionWith(item), VendorId, Now);

        Assert.Single(result.PriorMeetingNotes);
        Assert.Empty(result.OpenCommitments);
        Assert.Equal(DocType.OwnerNote, result.PriorMeetingNotes[0].DocType);
    }

    // ── OpenQuestion (any confidence) → EvidenceGaps ──────────────────────────

    [Fact]
    public void Enrich_OpenQuestion_AddsToEvidenceGaps()
    {
        var item = MakeItem(TranscriptItemType.OpenQuestion,
            "SOC 2 certificate status unresolved",
            requiresConfirmation: false);

        var result = TranscriptEvidenceAdapter.Enrich(EmptyBundle(), ExtractionWith(item), VendorId, Now);

        Assert.Empty(result.OpenCommitments);
        Assert.Single(result.EvidenceGaps);
        Assert.Equal("SOC 2 certificate status unresolved", result.EvidenceGaps[0]);
    }

    // ── Medium-confidence item → EvidenceGaps with prefix ─────────────────────

    [Fact]
    public void Enrich_MediumConfCommitment_AddsToGapsWithPrefix()
    {
        var item = MakeItem(TranscriptItemType.Commitment,
            "May deliver usage report",
            requiresConfirmation: true); // medium-confidence

        var result = TranscriptEvidenceAdapter.Enrich(EmptyBundle(), ExtractionWith(item), VendorId, Now);

        Assert.Empty(result.OpenCommitments);
        Assert.Single(result.EvidenceGaps);
        Assert.StartsWith("Requires confirmation:", result.EvidenceGaps[0]);
        Assert.Contains("May deliver usage report", result.EvidenceGaps[0]);
    }

    // ── Unresolved pre-brief item → EvidenceGaps ──────────────────────────────

    [Fact]
    public void Enrich_UnresolvedPreBriefItem_AddsToGaps()
    {
        var resolution = new PreBriefItemResolution(
            PreBriefItem:        "7% pricing uplift",
            AddressedInMeeting:  false,
            TranscriptEvidence:  null,
            TranscriptTimestamp: null,
            Confidence:          0.0);

        var extraction = new TranscriptExtractionResult(
            Items:                 [],
            ResolvedPreBriefItems: [resolution],
            Metadata:              EmptyMeta());

        var result = TranscriptEvidenceAdapter.Enrich(EmptyBundle(), extraction, VendorId, Now);

        Assert.Single(result.EvidenceGaps);
        Assert.Contains("7% pricing uplift", result.EvidenceGaps[0]);
        Assert.StartsWith("Not addressed in meeting:", result.EvidenceGaps[0]);
    }

    // ── Resolved pre-brief item → NOT added to gaps ────────────────────────────

    [Fact]
    public void Enrich_ResolvedPreBriefItem_NotAddedToGaps()
    {
        var resolution = new PreBriefItemResolution(
            PreBriefItem:        "SLA report",
            AddressedInMeeting:  true,
            TranscriptEvidence:  "Vendor confirmed delivery by Friday",
            TranscriptTimestamp: TimeSpan.FromMinutes(5),
            Confidence:          0.9);

        var extraction = new TranscriptExtractionResult(
            Items:                 [],
            ResolvedPreBriefItems: [resolution],
            Metadata:              EmptyMeta());

        var result = TranscriptEvidenceAdapter.Enrich(EmptyBundle(), extraction, VendorId, Now);

        Assert.Empty(result.EvidenceGaps);
    }

    // ── Existing bundle evidence is preserved ──────────────────────────────────

    [Fact]
    public void Enrich_PreservesExistingBundleEvidence()
    {
        var existingCommitment = MakeSyntheticEvidence(DocType.OwnerNote);
        var bundle = EmptyBundle() with { OpenCommitments = [existingCommitment] };

        var newItem = MakeItem(TranscriptItemType.Commitment,
            "New commitment from transcript",
            requiresConfirmation: false);

        var result = TranscriptEvidenceAdapter.Enrich(bundle, ExtractionWith(newItem), VendorId, Now);

        Assert.Equal(2, result.OpenCommitments.Count);
        Assert.Contains(existingCommitment, result.OpenCommitments);
    }

    // ── Multiple items of different types in one call ─────────────────────────

    [Fact]
    public void Enrich_MixedItems_RoutedCorrectly()
    {
        var items = new[]
        {
            MakeItem(TranscriptItemType.Commitment,    "Send SLA report",      requiresConfirmation: false),
            MakeItem(TranscriptItemType.PricingSignal, "Price increase hinted", requiresConfirmation: false),
            MakeItem(TranscriptItemType.OpenQuestion,  "Renewal process unclear", requiresConfirmation: false),
            MakeItem(TranscriptItemType.Commitment,    "Maybe deliver docs",    requiresConfirmation: true),
        };
        var extraction = new TranscriptExtractionResult(
            Items:                 items,
            ResolvedPreBriefItems: [],
            Metadata:              EmptyMeta());

        var result = TranscriptEvidenceAdapter.Enrich(EmptyBundle(), extraction, VendorId, Now);

        Assert.Single(result.OpenCommitments);   // high-conf commitment
        Assert.Single(result.CommercialSignals);  // high-conf pricing signal
        Assert.Equal(2, result.EvidenceGaps.Count); // open question + medium-conf commitment
    }

    // ── SanitizeRef ───────────────────────────────────────────────────────────

    [Fact]
    public void SanitizeRef_RemovesDotsAndDashes()
    {
        Assert.Equal("Vendor agreed 5 0 uplift", TranscriptEvidenceAdapter.SanitizeRef("Vendor agreed 5.0 uplift"));
        Assert.Equal("SLA report by Friday", TranscriptEvidenceAdapter.SanitizeRef("SLA report-by-Friday"));
    }

    [Fact]
    public void SanitizeRef_TrimsWhitespace()
    {
        Assert.Equal("Vendor agreed", TranscriptEvidenceAdapter.SanitizeRef("  Vendor agreed  "));
    }

    [Fact]
    public void SanitizeRef_PlainDescription_UnchangedExceptTrim()
    {
        const string desc = "Vendor agreed to send SLA report by Friday";
        Assert.Equal(desc, TranscriptEvidenceAdapter.SanitizeRef(desc));
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static VendorCallEvidenceBundle EmptyBundle()
        => new([], [], [], [], [], [], []);

    private static TranscriptExtractionResult EmptyExtraction()
        => new([], [], EmptyMeta());

    private static TranscriptExtractionMetadata EmptyMeta()
        => new(0, 0, 0, 0, 0, TimeSpan.Zero);

    private static TranscriptExtractedItem MakeItem(
        TranscriptItemType type,
        string             description,
        bool               requiresConfirmation)
        => new(
            Type:                     type,
            Description:              description,
            Speaker:                  "Test Speaker",
            CounterParty:             null,
            Quote:                    description,
            TranscriptTimestamp:      TimeSpan.FromMinutes(5),
            Confidence:               requiresConfirmation ? 0.65 : 0.90,
            ClaimKey:                 "vendor.commitment.description",
            Owner:                    null,
            DueDate:                  null,
            RequiresUserConfirmation: requiresConfirmation);

    private static TranscriptExtractionResult ExtractionWith(params TranscriptExtractedItem[] items)
        => new(items, [], EmptyMeta());

    private static Evidence MakeSyntheticEvidence(DocType docType)
        => new(
            EvidenceId: Guid.NewGuid(),
            VendorId:   VendorId,
            DocType:    docType,
            SourceTier: SourceTier.Reported,
            Ref:        "existing-commitment",
            DocVersion: 1,
            IngestedAt: Now);
}
