namespace Po.VendorCall;

public sealed class InMemoryVendorUpdateNoteStore : IVendorUpdateNoteStore
{
    private readonly List<VendorUpdateNote> _notes = [];

    public Task SaveAsync(VendorUpdateNote note, CancellationToken ct)
    {
        _notes.Add(note);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<VendorUpdateNote>> GetForVendorAsync(
        Guid vendorId, CancellationToken ct)
    {
        IReadOnlyList<VendorUpdateNote> result = _notes
            .Where(n => n.VendorId == vendorId)
            .OrderBy(n => n.SubmittedAtUtc)
            .ToList();
        return Task.FromResult(result);
    }
}
