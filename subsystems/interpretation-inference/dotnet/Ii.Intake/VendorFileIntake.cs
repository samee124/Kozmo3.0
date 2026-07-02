using Ii.Contracts;
using Kozmo.Contracts;
using Kozmo.Contracts.Config;
using Km.Store;

namespace Ii.Intake;

/// <summary>
/// Orchestrates the three vendor-file intake lanes for one vendor.
///
/// Lane routing:
///   PRIMARY evidence  (contract)  → PdfIntakeLane.Replay     → PRIMARY beliefs
///   VERIFIED evidence (CSV)       → RulesExtractor.Extract   → VERIFIED beliefs
///   Feedback signals              → IObservationModule       → REPORTED beliefs
///                                                              (§4 scored claim_keys only)
///
/// The feedback (signals) lane is the EXISTING Observation classifier reused exactly as the
/// live signal path uses it — the classifier is stateless. The difference from the live path:
///   - Live path:  ClassificationResult → Ii.Spine → Belief(ClaimKey="")  (posture path)
///   - This lane:  ClassificationResult → criterion→claim_key map → Belief(ClaimKey="csat"…)
///                 written via WriteBeliefAsync as an ADDITIONAL write.
///
/// The ClaimKey="" signal→posture path is untouched; this lane writes alongside it.
/// </summary>
public sealed class VendorFileIntake
{
    private readonly VendorFileWriteService _writeService;
    private readonly SaasProfile           _profile;
    private readonly PdfIntakeLane         _pdfLane;
    private readonly RulesExtractor        _rulesExtractor;
    private readonly IObservationModule    _observation;

    // Maps the Observation criterion name to the §4 claim_key.
    // Only criteria that have a matching §4 scored claim_key appear here;
    // everything else is silently skipped (no §4 claim_key → no vendor-file write).
    private static readonly IReadOnlyDictionary<string, string> CriterionToClaimKey =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["csat_score"]            = "csat",
            ["uptime_sla"]            = "sla_uptime",
            ["roadmap_alignment"]     = "roadmap_alignment",
            ["renewal_intent"]        = "renewal_intent",
            ["invoice_accuracy"]      = "invoice_accuracy",
            ["support_response_time"] = "support_responsiveness",
        };

    public VendorFileIntake(
        VendorFileWriteService writeService,
        SaasProfile            profile,
        IObservationModule     observation)
    {
        _writeService   = writeService;
        _profile        = profile;
        _observation    = observation;
        _pdfLane        = new PdfIntakeLane(profile);
        _rulesExtractor = new RulesExtractor(profile);
    }

    /// <summary>
    /// Ingest evidence + signals for one vendor.
    ///
    /// evidenceBlocks: Primary/Verified evidence blocks; each carries the evidence metadata,
    ///   a pre-extracted claims fixture ({"claims":[…]} JSON), and the document's effective
    ///   observed_at. Routed by DocTypeTierMap.
    ///
    /// feedbackSignals: feedback / email signals for the signals lane. Each is paired with its
    ///   Evidence record (provides evidenceId and ingestion metadata for provenance).
    ///   Observation.Classify runs on each; results mapping to a §4 scored claim_key emit a
    ///   REPORTED belief alongside the unchanged ClaimKey="" signal→posture path.
    ///
    /// Returns every belief written, in lane order.
    /// </summary>
    public async Task<IReadOnlyList<Belief>> IngestAsync(
        Guid vendorId,
        IEnumerable<(Evidence evidence, string claimsFixtureJson, DateTimeOffset observedAt)> evidenceBlocks,
        IEnumerable<(Signal signal, Evidence evidence)> feedbackSignals,
        CancellationToken ct = default)
    {
        var all = new List<Belief>();

        // ── Evidence lanes: PDF (Primary) and Rules (Verified) ───────────────
        foreach (var (ev, claimsJson, observedAt) in evidenceBlocks)
        {
            var claims  = ExtractEvidenceClaims(ev, claimsJson, observedAt);
            var beliefs = await WriteClaimsAsync(vendorId, ev, claims, ct);
            all.AddRange(beliefs);
        }

        // ── Signals lane: Observation classifier → REPORTED beliefs ──────────
        foreach (var (signal, evidence) in feedbackSignals)
        {
            var belief = await ClassifyAndWriteAsync(vendorId, signal, evidence, ct);
            if (belief != null) all.Add(belief);
        }

        return all;
    }

    // ── Evidence lane routing ─────────────────────────────────────────────────

    private IReadOnlyList<ExtractedClaim> ExtractEvidenceClaims(
        Evidence ev, string claimsJson, DateTimeOffset observedAt)
    {
        _profile.DocTypeTierMap.TryGetValue(ev.DocType.ToString(), out var docTier);

        return docTier == "Primary"
            ? _pdfLane.Replay(ev, claimsJson, observedAt)
            : _rulesExtractor.Extract(ev, claimsJson, observedAt);
    }

    private async Task<IReadOnlyList<Belief>> WriteClaimsAsync(
        Guid vendorId, Evidence ev,
        IReadOnlyList<ExtractedClaim> claims, CancellationToken ct)
    {
        var results = new List<Belief>();
        foreach (var claim in claims)
        {
            if (!_profile.ClaimKeyCatalogue.TryGetValue(claim.ClaimKey, out var ckDef)) continue;

            // Structural claims have empty dimension string in the catalogue.
            // Use Financial as a placeholder — confidence=0 means they never feed the rubric.
            if (!Enum.TryParse<Dimension>(ckDef.Dimension, ignoreCase: true, out var dimension))
                dimension = Dimension.Financial;

            var belief = await _writeService.WriteBeliefAsync(
                vendorId:            vendorId,
                claimKey:            claim.ClaimKey,
                dimension:           dimension,
                criterion:           claim.ClaimKey,
                rawValue:            claim.NormalisedValue,
                tier:                claim.Tier,
                extractorConfidence: claim.ExtractorConfidence,
                observedAt:          claim.ObservedAt,
                provenance:          new BeliefProvenance(claim.EvidenceId, claim.Locator),
                ingestedAt:          ev.IngestedAt,
                ct:                  ct);

            results.Add(belief);
        }
        return results;
    }

    // ── Signals lane ──────────────────────────────────────────────────────────

    private async Task<Belief?> ClassifyAndWriteAsync(
        Guid vendorId, Signal signal, Evidence evidence, CancellationToken ct)
    {
        // Run the EXISTING Observation classifier exactly as the live signal path does.
        var result = _observation.Classify(signal, _profile);
        if (result == null) return null;

        // Map criterion → §4 claim_key. If no mapping exists the signal has no vendor-file slot.
        if (!CriterionToClaimKey.TryGetValue(result.Criterion, out var claimKey)) return null;

        // Verify the claim_key is a scored §4 entry (structural slots have no scorer slot).
        if (!_profile.ClaimKeyCatalogue.TryGetValue(claimKey, out var ckDef)) return null;
        if (ckDef.ClaimClass != "scored") return null;

        if (!Enum.TryParse<Dimension>(ckDef.Dimension, ignoreCase: true, out var dimension))
            return null;

        // Tier is FORCED to REPORTED (feedback/email source) regardless of what the classifier
        // returned (the classifier returns Verified for CRM/csat_score because it can be a
        // first-class VERIFIED signal on its own; the vendor-file write is always REPORTED
        // because it comes through the email/feedback evidence channel).
        const SourceTier reportedTier = SourceTier.Reported;

        // For rule-based classifications MethodConfidence is null; use 1.0 so the store's
        // tier ceiling (0.5 for Reported) becomes the effective confidence.
        var extractorConf = result.MethodConfidence ?? 1.0;

        // Provenance locator: message_ref:<signalId> (§6 signals locator form).
        var locator    = $"message_ref:{signal.Id}";
        var provenance = new BeliefProvenance(evidence.EvidenceId, locator);

        return await _writeService.WriteBeliefAsync(
            vendorId:            vendorId,
            claimKey:            claimKey,
            dimension:           dimension,
            criterion:           claimKey,      // vendor-file convention: criterion == claim_key
            rawValue:            result.Value,
            tier:                reportedTier,
            extractorConfidence: extractorConf,
            observedAt:          signal.ObservedAt,
            provenance:          provenance,
            ingestedAt:          evidence.IngestedAt,
            ct:                  ct);
    }
}
