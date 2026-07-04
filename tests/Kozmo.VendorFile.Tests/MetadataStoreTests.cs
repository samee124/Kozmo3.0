using Km.Store.Metadata;
using Xunit;

namespace Kozmo.VendorFile.Tests;

/// <summary>
/// E1 Part 7 Step 4 — proves the metadata store's access layer works, in isolation, before any
/// extraction path writes to it (Step 5). No belief, no scoring assembly, no extraction code is
/// touched here.
/// </summary>
public sealed class MetadataStoreTests
{
    [Fact, Trait("Category", "VendorFile")]
    public async Task WriteThenGetForEntity_RoundTrips()
    {
        using var store = new SqliteMetadataStore("Data Source=:memory:");
        var entityId   = Guid.NewGuid();
        var documentId = Guid.NewGuid();
        var observedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        var metadata = new DocumentMetadata(
            Id:           Guid.NewGuid(),
            EntityId:     entityId,
            DocumentId:   documentId,
            DocumentType: "invoice",
            FieldName:    "po_reference",
            Value:        "PO-2023-0044",
            Derivation:   "doc:Invoice_RGL-2023-002.txt \"PO Number: PRU-2023-0044\"",
            ObservedAt:   observedAt);

        await store.WriteAsync(metadata);

        var knowledge = await store.GetForEntityAsync(entityId);

        Assert.Equal(entityId, knowledge.EntityId);
        var written = Assert.Single(knowledge.Metadata);
        Assert.Equal(metadata, written);
    }

    [Fact, Trait("Category", "VendorFile")]
    public async Task GetForEntity_UnknownEntity_ReturnsEmpty()
    {
        using var store = new SqliteMetadataStore("Data Source=:memory:");

        var knowledge = await store.GetForEntityAsync(Guid.NewGuid());

        Assert.Empty(knowledge.Metadata);
    }

    [Fact, Trait("Category", "VendorFile")]
    public async Task GetForEntity_OnlyReturnsThatEntitysMetadata()
    {
        using var store = new SqliteMetadataStore("Data Source=:memory:");
        var entityA = Guid.NewGuid();
        var entityB = Guid.NewGuid();
        var now     = DateTimeOffset.UtcNow;

        await store.WriteAsync(new DocumentMetadata(
            Guid.NewGuid(), entityA, Guid.NewGuid(), "msa", "notice_period", "90 days", "doc:a", now));
        await store.WriteAsync(new DocumentMetadata(
            Guid.NewGuid(), entityB, Guid.NewGuid(), "msa", "notice_period", "30 days", "doc:b", now));

        var knowledge = await store.GetForEntityAsync(entityA);

        var only = Assert.Single(knowledge.Metadata);
        Assert.Equal("90 days", only.Value);
    }
}
