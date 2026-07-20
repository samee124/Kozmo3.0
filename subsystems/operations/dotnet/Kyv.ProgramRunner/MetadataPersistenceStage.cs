using Ig.Contracts;
using Ii.CandidateExtraction;
using Km.Store.Metadata;

namespace Kyv.ProgramRunner;

/// <summary>
/// E1 Part 7 Step 5 — correlates doc-scoped MetadataCandidates (produced by
/// DocumentBeliefExtractor during Stage 3, alongside BeliefCandidates) to the vendor each
/// originating document resolved to, via the SAME ClusterId &lt;- DocId path
/// BeliefPersistenceStage and RegistryWriter.Build() use, then writes them through
/// IMetadataStore. Deliberately a separate stage/store from BeliefPersistenceStage/IEntityStore —
/// metadata is never scored and never read by scoring (the CI wall lane in
/// Kozmo.Architecture.Tests enforces this at the assembly level).
/// </summary>
public sealed class MetadataPersistenceStage
{
    private readonly IMetadataStore _store;

    public MetadataPersistenceStage(IMetadataStore store) => _store = store;

    /// <summary>
    /// Persists every MetadataCandidate whose originating document resolved to a vendor (any
    /// disposition other than NonVendor). Returns the number of metadata rows written.
    /// </summary>
    public async Task<int> PersistAsync(
        IReadOnlyList<(string DocId, IReadOnlyList<MetadataCandidate> Candidates)> candidatesByDoc,
        IReadOnlyList<CandidateCluster>      clusters,
        IReadOnlyList<ResolutionDisposition> dispositions,
        DateTimeOffset                       now,
        IReadOnlyDictionary<string, Guid>?   docIdToDocumentId = null,
        CancellationToken                    ct = default)
    {
        var docIdToEntityId = BuildDocIdToEntityMap(clusters, dispositions);
        var written = 0;

        foreach (var (docId, candidates) in candidatesByDoc)
        {
            if (candidates.Count == 0) continue;
            if (!docIdToEntityId.TryGetValue(docId, out var entityId)) continue;

            var documentType = DocTypeInferrer.InferDocType(docId);
            // Document retention (see KYV_KNOWN_GAPS.md): when DocumentPersistenceStage ran for
            // this document, reuse its real, stable documents.id — falls back to a fresh Guid only
            // when no document store was supplied to this runner (documentStore: null, e.g.
            // existing tests), preserving the exact prior placeholder behavior for those callers.
            var documentId = docIdToDocumentId != null && docIdToDocumentId.TryGetValue(docId, out var docGuid)
                ? docGuid
                : Guid.NewGuid();

            foreach (var candidate in candidates)
            {
                await _store.WriteAsync(new DocumentMetadata(
                    Id:           Guid.NewGuid(),
                    EntityId:     entityId,
                    DocumentId:   documentId,
                    DocumentType: documentType,
                    FieldName:    candidate.FieldName,
                    Value:        candidate.Value,
                    Derivation:   candidate.Derivation,
                    ObservedAt:   now), ct);

                written++;
            }
        }

        return written;
    }

    // ── DocId -> EntityId correlation (same path BeliefPersistenceStage/RegistryWriter use) ──

    private static IReadOnlyDictionary<string, Guid> BuildDocIdToEntityMap(
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
