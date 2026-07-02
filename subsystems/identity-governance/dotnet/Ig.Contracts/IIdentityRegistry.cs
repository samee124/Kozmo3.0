namespace Ig.Contracts;

public interface IIdentityRegistry
{
    Task SaveAsync(CanonicalVendor vendor, CancellationToken ct = default, Guid? programRunId = null);
    Task<CanonicalVendor?> GetAsync(Guid vendorId, CancellationToken ct = default);
    Task<IReadOnlyList<CanonicalVendor>> GetAllAsync(CancellationToken ct = default);
    Task MarkAbsorbedAsync(Guid vendorId, Guid survivorVendorId, CancellationToken ct = default);
}
