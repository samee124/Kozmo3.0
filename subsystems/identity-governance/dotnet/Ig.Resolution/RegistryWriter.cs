using Ig.Contracts;

// §0 REUSE: writes through IIdentityRegistry.SaveAsync() which calls
// IRegistryStore.SaveRegistryVendorAsync + SaveVendorAliasAsync (the existing
// append-and-supersede SQLite write path built in Commit 0b).
// No parallel persistence path is introduced here.

namespace Ig.Resolution;

/// <summary>
/// Stage F — Write to Registry.
/// Converts each (cluster, disposition) pair to a CanonicalVendor and persists it via
/// the existing IIdentityRegistry write path. Each alias records the raw_name exactly
/// as written in the source document, with provenance, so run-two recognition can match
/// against known aliases without re-extracting. Do NOT pass DateTimeOffset.UtcNow from
/// inside this class — the caller supplies 'now' to keep Stage F deterministic and testable.
/// </summary>
public sealed class RegistryWriter
{
    private readonly IIdentityRegistry _registry;

    public RegistryWriter(IIdentityRegistry registry) => _registry = registry;

    /// <summary>
    /// Persists all clusters under their dispositioned status and returns the
    /// dispositions unchanged (for the caller to route to Phase 3 if needed).
    /// Ordering: AUTO_CONFIRM first, then PROVISIONAL, then TRIAGE — but since
    /// clusters are independent this is informational only.
    /// </summary>
    public async Task<IReadOnlyList<ResolutionDisposition>> WriteAsync(
        IReadOnlyList<CandidateCluster>      clusters,
        IReadOnlyList<ResolutionDisposition> dispositions,
        DateTimeOffset                       now,
        Guid?                                programRunId = null,
        CancellationToken                    ct           = default)
    {
        for (int i = 0; i < clusters.Count; i++)
        {
            if (dispositions[i].Disposition == Ig.Contracts.Disposition.NonVendor) continue;
            await _registry.SaveAsync(Build(clusters[i], dispositions[i], now), ct, programRunId);
        }

        return dispositions;
    }

    // ── Builder ────────────────────────────────────────────────────────────────

    private static CanonicalVendor Build(
        CandidateCluster    cluster,
        ResolutionDisposition disposition,
        DateTimeOffset      now)
    {
        var aliases = cluster.Members.Select(m =>
            new VendorAlias(
                AliasId:         Guid.NewGuid(),
                VendorId:        cluster.ClusterId,
                RawName:         m.Normalized.Candidate.RawName,
                ProvenanceDocId: m.Normalized.Candidate.Provenance.DocId,
                ProvenanceSpan:  m.Normalized.Candidate.Provenance.Span))
            .ToList();

        var status = disposition.Disposition switch
        {
            Ig.Contracts.Disposition.AutoConfirm => RegistryStatus.Confirmed,
            Ig.Contracts.Disposition.Provisional => RegistryStatus.Provisional,
            _                                    => RegistryStatus.Triage,
        };

        return new CanonicalVendor(
            VendorId:          cluster.ClusterId,
            CanonicalName:     cluster.CanonicalName,
            Aliases:           aliases,
            ComparisonKey:     cluster.ComparisonKey,
            EntityType:        cluster.EntityType,
            Confidence:        cluster.Confidence,
            Flags:             disposition.Flags,       // carries Stage D + Stage E flags
            Status:            status,
            RebrandMapRef:     null,                    // empty this phase
            AcquisitionMapRef: null,
            CreatedAt:         now,
            EntityRole:        cluster.EntityRole);      // Stage C's role, carried through instead
                                                          // of discarded (E-signal Part 5 Step 3)
    }
}
