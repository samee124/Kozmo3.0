namespace Km.Store.Metadata;

/// <summary>
/// Access layer for the metadata store (E1 Part 7 Step 4). Append-only by convention, matching the
/// belief store's discipline — though metadata carries no confidence/tier semantics of its own, so
/// there is no supersession collision to resolve; each write is simply retained.
/// <para>
/// Not called by any extraction path yet. Step 4 ships the store and its CI wall
/// (<c>Kozmo.Architecture.Tests</c>'s metadata-wall invariant) before any data flows, so the
/// isolation is proven before there is anything to isolate.
/// </para>
/// </summary>
public interface IMetadataStore
{
    Task WriteAsync(DocumentMetadata metadata, CancellationToken ct = default);

    Task<EntityKnowledge> GetForEntityAsync(Guid entityId, CancellationToken ct = default);
}
