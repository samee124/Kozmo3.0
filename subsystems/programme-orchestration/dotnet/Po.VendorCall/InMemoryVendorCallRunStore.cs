namespace Po.VendorCall;

/// <summary>In-memory implementation of IVendorCallRunStore for use in tests.</summary>
public sealed class InMemoryVendorCallRunStore : IVendorCallRunStore
{
    private readonly Dictionary<string, VendorCallRun> _runs = new();

    public Task<VendorCallRun?> GetByIdAsync(Guid id, CancellationToken ct)
        => Task.FromResult(_runs.Values.FirstOrDefault(r => r.Id == id));

    public Task<VendorCallRun?> GetByEventIdAsync(string eventId, CancellationToken ct)
        => Task.FromResult(_runs.TryGetValue(eventId, out var run) ? run : null);

    public Task<IReadOnlyList<VendorCallRun>> GetByStatusAsync(VendorCallStatus status, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<VendorCallRun>>(
            _runs.Values.Where(r => r.Status == status).ToList());

    public Task<IReadOnlyList<VendorCallRun>> GetByVendorIdAsync(Guid vendorId, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<VendorCallRun>>(
            _runs.Values.Where(r => r.VendorId == vendorId).ToList());

    public Task SaveAsync(VendorCallRun run, CancellationToken ct)
    {
        _runs[run.EventId] = run;
        return Task.CompletedTask;
    }
}
