namespace Km.Store;

/// <summary>
/// Storage interface for retained source documents (documents table) — the fix for beliefs/clauses
/// that previously had no way to link back to an actual source file (see KYV_KNOWN_GAPS.md's
/// document-retention diagnosis). Separate, optional interface — mirrors IRegistryStore/
/// ICheckInRowStore/IMetadataStore's shape, implemented additionally by SqliteEntityStore.
/// </summary>
public interface IDocumentStore
{
    Task UpsertDocumentAsync(DocumentRow doc, CancellationToken ct = default);
    Task<DocumentRow?> GetDocumentAsync(Guid id, CancellationToken ct = default);
}

/// <summary>
/// Storage row for a retained source document (documents table). All three retention options from
/// the diagnosis are additive, not exclusive: Content (raw PDF bytes), ContentText (extracted
/// text), and DriveFileId (durable Drive pointer) can all be populated together for one document.
/// ProgramId/VendorId are nullable — a document may not yet resolve to either at write time.
/// </summary>
public sealed record DocumentRow(
    Guid           Id,
    Guid?          ProgramId,
    Guid?          VendorId,
    string         Filename,
    byte[]?        Content,
    string?        ContentText,
    string?        DriveFileId,
    DateTimeOffset IngestedAt
);
