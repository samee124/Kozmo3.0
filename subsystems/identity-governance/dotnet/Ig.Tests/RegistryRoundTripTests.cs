using Ig.Contracts;
using Ig.Resolution;
using Km.Store;
using Xunit;

namespace Ig.Tests;

public sealed class RegistryRoundTripTests : IDisposable
{
    private readonly SqliteEntityStore _store;
    private readonly IdentityRegistry  _registry;

    public RegistryRoundTripTests()
    {
        _store    = new SqliteEntityStore("Data Source=:memory:");
        _registry = new IdentityRegistry(_store);
    }

    public void Dispose() => _store.Dispose();

    [Fact]
    public async Task SaveAndGet_RoundTripsAllFields()
    {
        var vendorId = Guid.NewGuid();
        var alias1   = new VendorAlias(Guid.NewGuid(), vendorId, "Acme Corp",        "doc-001", "p.1");
        var alias2   = new VendorAlias(Guid.NewGuid(), vendorId, "Acme Corporation", null,      null);

        var vendor = new CanonicalVendor(
            VendorId:          vendorId,
            CanonicalName:     "Acme Corp Ltd",
            Aliases:           [alias1, alias2],
            ComparisonKey:     "acme-corp-ltd",
            EntityType:        EntityType.Company,
            Confidence:        0.85,
            Flags:             ["rebrand", "acquisition-pending"],
            Status:            RegistryStatus.Confirmed,
            RebrandMapRef:     "v-old-001",
            AcquisitionMapRef: null,
            CreatedAt:         new DateTimeOffset(2025, 1, 15, 0, 0, 0, TimeSpan.Zero),
            EntityRole:        "vendor");

        await _registry.SaveAsync(vendor);

        var retrieved = await _registry.GetAsync(vendorId);

        Assert.NotNull(retrieved);
        Assert.Equal(vendorId,            retrieved.VendorId);
        Assert.Equal("Acme Corp Ltd",     retrieved.CanonicalName);
        Assert.Equal("acme-corp-ltd",     retrieved.ComparisonKey);
        Assert.Equal(EntityType.Company,  retrieved.EntityType);
        Assert.Equal(0.85,                retrieved.Confidence, precision: 10);
        Assert.Equal(RegistryStatus.Confirmed, retrieved.Status);
        Assert.Equal("v-old-001",         retrieved.RebrandMapRef);
        Assert.Null(retrieved.AcquisitionMapRef);
        Assert.Equal("vendor",            retrieved.EntityRole);
        Assert.Equal(2, retrieved.Aliases.Count);
        Assert.Contains(retrieved.Aliases, a => a.RawName == "Acme Corp" && a.ProvenanceDocId == "doc-001");
        Assert.Contains(retrieved.Aliases, a => a.RawName == "Acme Corporation" && a.ProvenanceDocId == null);
        Assert.Equal(2, retrieved.Flags.Count);
        Assert.Contains("rebrand", retrieved.Flags);
    }

    [Fact]
    public async Task GetAll_ReturnsAllSavedVendors()
    {
        var id1 = Guid.NewGuid();
        var id2 = Guid.NewGuid();

        await _registry.SaveAsync(MakeVendor(id1, "Alpha Systems", RegistryStatus.Confirmed));
        await _registry.SaveAsync(MakeVendor(id2, "Beta Services", RegistryStatus.Provisional));

        var all = await _registry.GetAllAsync();

        Assert.Equal(2, all.Count);
        Assert.Contains(all, v => v.VendorId == id1 && v.CanonicalName == "Alpha Systems");
        Assert.Contains(all, v => v.VendorId == id2 && v.CanonicalName == "Beta Services");
    }

    [Fact]
    public async Task GetAsync_ReturnsNull_WhenNotFound()
    {
        var result = await _registry.GetAsync(Guid.NewGuid());
        Assert.Null(result);
    }

    [Fact]
    public async Task SaveAndGet_LegacyVendor_EntityRoleDefaultsNull()
    {
        // A vendor saved without EntityRole (the field's default) round-trips as null — the
        // legacy shape, unaffected by E-signal Part 5 Step 3's addition.
        var id = Guid.NewGuid();
        await _registry.SaveAsync(MakeVendor(id, "Legacy Vendor Co", RegistryStatus.Confirmed));

        var retrieved = await _registry.GetAsync(id);

        Assert.NotNull(retrieved);
        Assert.Null(retrieved.EntityRole);
    }

    private static CanonicalVendor MakeVendor(Guid id, string name, RegistryStatus status) =>
        new CanonicalVendor(
            VendorId:          id,
            CanonicalName:     name,
            Aliases:           [],
            ComparisonKey:     null,
            EntityType:        EntityType.Unknown,
            Confidence:        0.5,
            Flags:             [],
            Status:            status,
            RebrandMapRef:     null,
            AcquisitionMapRef: null,
            CreatedAt:         DateTimeOffset.UtcNow);
}
