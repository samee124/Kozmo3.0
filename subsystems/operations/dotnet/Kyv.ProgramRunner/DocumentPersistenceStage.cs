using System.Security.Cryptography;
using System.Text;
using Ig.Contracts;
using Km.Store;

namespace Kyv.ProgramRunner;

/// <summary>
/// Document retention (see KYV_KNOWN_GAPS.md's document-retention diagnosis) — persists the raw
/// bytes, extracted text, and Drive file id for every downloaded document BEFORE Program.cs's
/// /kyv/run handler deletes the temp folder, so a belief/clause can finally link back to its actual
/// source instead of a dangling filename string. Runs alongside BeliefPersistenceStage/
/// MetadataPersistenceStage at Stage 6 — same doc-scoped ClusterId &lt;- DocId correlation
/// (RegistryWriter.Build()'s path) — and its returned docId -&gt; documentId map feeds both of those
/// stages so BeliefProvenance.EvidenceId/DocumentMetadata.DocumentId point at a real row instead of
/// Guid.Empty/a fresh Guid.NewGuid() per run.
/// </summary>
public sealed class DocumentPersistenceStage
{
    private readonly IDocumentStore _store;

    public DocumentPersistenceStage(IDocumentStore store) => _store = store;

    /// <summary>
    /// Deterministic document id — prefers the Drive file id (immutable and unique per Google
    /// file, so re-ingesting the same Drive file always maps to the same row, even if its content
    /// was edited in Drive between runs); falls back to a content hash when no Drive id exists
    /// (manual uploads, fixture-based test runs) — identical bytes always resolve to the same id,
    /// different bytes (even under the same filename) get a new one. Never a fresh random Guid —
    /// that was the exact bug this stage fixes (MetadataPersistenceStage's prior
    /// "no stable per-document identity" comment).
    /// </summary>
    public static Guid ComputeDocumentId(string? driveFileId, byte[] content)
    {
        var key = driveFileId ?? ("content:" + Convert.ToHexString(SHA256.HashData(content)));
        var hash = MD5.HashData(Encoding.UTF8.GetBytes(key));
        return new Guid(hash);
    }

    /// <summary>
    /// Upserts (INSERT OR REPLACE, keyed on the deterministic id) a documents row for every
    /// captured document, regardless of whether it resolved to a vendor — retention is about the
    /// source, not extraction success, so even a NonVendor-dispositioned or unreadable document
    /// still gets a row (VendorId left null). Returns the docId -&gt; documentId map for
    /// BeliefPersistenceStage/MetadataPersistenceStage to correlate against.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, Guid>> PersistAsync(
        IReadOnlyList<(string DocId, byte[] Content, string ContentText, string? DriveFileId)> documents,
        IReadOnlyList<CandidateCluster>      clusters,
        IReadOnlyList<ResolutionDisposition> dispositions,
        DateTimeOffset                       now,
        CancellationToken                    ct = default)
    {
        var docIdToVendorId = BuildDocIdToVendorMap(clusters, dispositions);
        var result = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);

        foreach (var (docId, content, contentText, driveFileId) in documents)
        {
            var documentId = ComputeDocumentId(driveFileId, content);
            var hasVendor  = docIdToVendorId.TryGetValue(docId, out var vendorId);

            await _store.UpsertDocumentAsync(new DocumentRow(
                Id:          documentId,
                ProgramId:   null, // not wired yet — schema-ready, not populated by this build
                VendorId:    hasVendor ? vendorId : null,
                Filename:    docId,
                Content:     content,
                ContentText: string.IsNullOrEmpty(contentText) ? null : contentText,
                DriveFileId: driveFileId,
                IngestedAt:  now), ct);

            result[docId] = documentId;
        }

        return result;
    }

    // ── DocId -> VendorId correlation (same path BeliefPersistenceStage/RegistryWriter use) ──

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
