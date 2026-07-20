using System.Text.Json;
using Ig.Contracts;
using Km.Store;

namespace Ig.Resolution;

/// <summary>
/// Persists and fetches CanonicalVendor records via Km.Store.
/// No resolution, clustering, or gating logic — scaffold only.
/// </summary>
public sealed class IdentityRegistry : IIdentityRegistry
{
    private readonly IRegistryStore _store;
    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    public IdentityRegistry(IRegistryStore store) => _store = store;

    public async Task SaveAsync(CanonicalVendor vendor, CancellationToken ct = default, Guid? programRunId = null)
    {
        var row = new RegistryVendorRow(
            VendorId:             vendor.VendorId,
            CanonicalName:        vendor.CanonicalName,
            CreatedAt:            vendor.CreatedAt,
            ComparisonKey:        vendor.ComparisonKey,
            EntityType:           vendor.EntityType.ToString(),
            Confidence:           vendor.Confidence,
            FlagsJson:            vendor.Flags.Count > 0
                                      ? JsonSerializer.Serialize(vendor.Flags, _json)
                                      : null,
            Status:               vendor.Status.ToString(),
            RebrandMapRef:        vendor.RebrandMapRef,
            AcquisitionMapRef:    vendor.AcquisitionMapRef,
            AbsorbedIntoVendorId: vendor.AbsorbedIntoVendorId,
            ProgramRunId:         programRunId,
            EntityRole:           vendor.EntityRole,
            DomainsJson:          vendor.KnownDomains.Count > 0
                                      ? JsonSerializer.Serialize(vendor.KnownDomains, _json)
                                      : null);

        await _store.SaveRegistryVendorAsync(row, ct);

        foreach (var alias in vendor.Aliases)
            await _store.SaveVendorAliasAsync(
                new VendorAliasRow(
                    AliasId:         alias.AliasId,
                    VendorId:        alias.VendorId,
                    RawName:         alias.RawName,
                    ProvenanceDocId: alias.ProvenanceDocId,
                    ProvenanceSpan:  alias.ProvenanceSpan),
                ct);
    }

    public async Task<CanonicalVendor?> GetAsync(Guid vendorId, CancellationToken ct = default)
    {
        var row = await _store.GetRegistryVendorAsync(vendorId, ct);
        if (row is null) return null;

        var aliasRows = await _store.GetVendorAliasesAsync(vendorId, ct);
        return Map(row, aliasRows);
    }

    public async Task<IReadOnlyList<CanonicalVendor>> GetAllAsync(CancellationToken ct = default)
    {
        var rows = await _store.GetAllRegistryVendorsAsync(ct);
        var result = new List<CanonicalVendor>(rows.Count);
        foreach (var row in rows)
        {
            if (row.Status == "Absorbed") continue;
            var aliasRows = await _store.GetVendorAliasesAsync(row.VendorId, ct);
            result.Add(Map(row, aliasRows));
        }
        return result;
    }

    public async Task MarkAbsorbedAsync(Guid vendorId, Guid survivorVendorId, CancellationToken ct = default)
    {
        var existing = await GetAsync(vendorId, ct);
        if (existing is null) return;
        await SaveAsync(existing with { Status = RegistryStatus.Absorbed, AbsorbedIntoVendorId = survivorVendorId }, ct);
    }

    private static CanonicalVendor Map(RegistryVendorRow row, IReadOnlyList<VendorAliasRow> aliasRows)
    {
        var aliases = aliasRows.Select(a => new VendorAlias(
            AliasId:         a.AliasId,
            VendorId:        a.VendorId,
            RawName:         a.RawName,
            ProvenanceDocId: a.ProvenanceDocId,
            ProvenanceSpan:  a.ProvenanceSpan)).ToList();

        var flags = row.FlagsJson is not null
            ? JsonSerializer.Deserialize<List<string>>(row.FlagsJson, _json) ?? []
            : (List<string>)[];

        var entityType = Enum.TryParse<EntityType>(row.EntityType, out var et)
            ? et : EntityType.Unknown;

        var status = Enum.TryParse<RegistryStatus>(row.Status, out var rs)
            ? rs : RegistryStatus.Triage;

        var domains = row.DomainsJson is not null
            ? JsonSerializer.Deserialize<List<string>>(row.DomainsJson, _json) ?? []
            : (List<string>)[];

        return new CanonicalVendor(
            VendorId:             row.VendorId,
            CanonicalName:        row.CanonicalName,
            Aliases:              aliases,
            ComparisonKey:        row.ComparisonKey,
            EntityType:           entityType,
            Confidence:           row.Confidence ?? 0.0,
            Flags:                flags,
            Status:               status,
            RebrandMapRef:        row.RebrandMapRef,
            AcquisitionMapRef:    row.AcquisitionMapRef,
            CreatedAt:            row.CreatedAt,
            AbsorbedIntoVendorId: row.AbsorbedIntoVendorId,
            EntityRole:           row.EntityRole)
        {
            KnownDomains = domains,
        };
    }
}
