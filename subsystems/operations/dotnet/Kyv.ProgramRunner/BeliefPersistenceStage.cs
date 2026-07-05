using Ig.Contracts;
using Ii.CandidateExtraction;
using Ii.Observation;
using Km.Store;
using Kozmo.Contracts;
using Kozmo.Contracts.Config;
using Kozmo.Contracts.Interfaces;

namespace Kyv.ProgramRunner;

/// <summary>
/// Stage 6 — persist_beliefs. Correlates doc-scoped BeliefCandidates (produced by
/// DocumentBeliefExtractor during Stage 3) to the vendor each originating document resolved to,
/// via the SAME ClusterId &lt;- DocId path RegistryWriter.Build() uses
/// (CandidateCluster.Members[].Normalized.Candidate.Provenance.DocId), then writes them through
/// VendorFileWriteService.
///
/// Value convention — see KYV_KNOWN_GAPS.md "Belief bridge Commit 1 -> Commit 2" — MUST NOT be
/// "simplified" into a single pass-through:
///   - Structural claims (payment_terms, renewal_date, annual_value) persist RAW.
///     VendorFileWriteService forces Confidence=0 for these, so RubricModule never scores them.
///   - Scored claims (sla_uptime, csat) are banded to 0-1 via the EXISTING proven scoring_rubric
///     config (ObservationModule.ScoreFromRubric) BEFORE persisting — a raw magnitude must never
///     reach a scored belief's Value.
/// </summary>
public sealed class BeliefPersistenceStage
{
    private readonly VendorFileWriteService _writeService;
    private readonly SaasProfile            _profile;

    public BeliefPersistenceStage(IEntityStore entityStore, SaasProfile profile)
    {
        _writeService = new VendorFileWriteService(entityStore, profile);
        _profile      = profile;
    }

    /// <summary>
    /// Persists every BeliefCandidate whose originating document resolved to a vendor (any
    /// disposition other than NonVendor). Candidates from documents that resolved to no cluster,
    /// or whose scored value is out of the rubric's domain, are skipped rather than persisted —
    /// this stage never invents a value it cannot ground. Returns the number of beliefs written.
    /// </summary>
    public async Task<int> PersistAsync(
        IReadOnlyList<(string DocId, IReadOnlyList<BeliefCandidate> Candidates)> candidatesByDoc,
        IReadOnlyList<CandidateCluster>      clusters,
        IReadOnlyList<ResolutionDisposition> dispositions,
        DateTimeOffset                       now,
        CancellationToken                    ct = default)
    {
        var docIdToVendorId = BuildDocIdToVendorMap(clusters, dispositions);
        var written = 0;

        foreach (var (docId, candidates) in candidatesByDoc)
        {
            if (!docIdToVendorId.TryGetValue(docId, out var vendorId)) continue;

            foreach (var candidate in candidates)
            {
                var value = BandIfScored(candidate.Criterion, candidate.Value);
                if (value is null) continue; // scored value outside the rubric's domain — abstain

                await _writeService.WriteBeliefAsync(
                    vendorId:            vendorId,
                    claimKey:            candidate.Criterion,
                    // candidate.Dimension is already the catalogue's declared dimension (e.g.
                    // Financial for annual_value/payment_terms, null for renewal_date and other
                    // dimensionless structural claims — see DocumentBeliefExtractor). The
                    // fallback only matters for the null case; VendorFileWriteService decides
                    // the persisted Dimension from the catalogue itself, not from this value.
                    dimension:           candidate.Dimension ?? Dimension.Financial,
                    criterion:           candidate.Criterion,
                    rawValue:            value.Value,
                    tier:                candidate.SourceTier,
                    extractorConfidence: candidate.Confidence,
                    observedAt:          now,
                    provenance:          new BeliefProvenance(Guid.Empty, candidate.Derivation), // no Evidence row in the KYV pipeline yet
                    ingestedAt:          now,
                    derivation:          candidate.Derivation, // real quoted evidence — see VendorFileWriteService docs
                    ct:                  ct);

                written++;
            }
        }

        return written;
    }

    // ── Value convention ───────────────────────────────────────────────────────

    /// <summary>
    /// Structural claim keys persist their raw magnitude unchanged. Scored claim keys are banded
    /// to 0-1 through the existing proven scoring_rubric config; a value outside that rubric's
    /// domain returns null (abstain), never a guessed score. The claim-key -> rubric-criterion
    /// translation (e.g. sla_uptime -> uptime_sla) comes from ClaimKeyDefinition.RubricCriterion —
    /// the same catalogue-driven field RulesExtractor uses (E1 Part 7 Step 7 Fix 4) — not a
    /// second, independently-maintained dictionary.
    /// </summary>
    private double? BandIfScored(string claimKey, double rawValue)
    {
        if (!_profile.ClaimKeyCatalogue.TryGetValue(claimKey, out var ckDef) || ckDef.ClaimClass != "scored")
            return rawValue; // structural — VendorFileWriteService zeroes confidence on write

        var rubricCriterion = ckDef.RubricCriterion ?? claimKey;
        return ObservationModule.ScoreFromRubric(rubricCriterion, rawValue, _profile);
    }

    // ── DocId -> VendorId correlation (same path RegistryWriter.Build() uses) ────

    private static IReadOnlyDictionary<string, Guid> BuildDocIdToVendorMap(
        IReadOnlyList<CandidateCluster>      clusters,
        IReadOnlyList<ResolutionDisposition> dispositions)
    {
        var map = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < clusters.Count; i++)
        {
            if (dispositions[i].Disposition == Disposition.NonVendor) continue;

            foreach (var member in clusters[i].Members)
                map[member.Normalized.Candidate.Provenance.DocId] = clusters[i].ClusterId;
        }
        return map;
    }
}
