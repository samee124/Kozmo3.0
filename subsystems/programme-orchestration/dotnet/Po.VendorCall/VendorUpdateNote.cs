namespace Po.VendorCall;

/// <summary>
/// A free-text context note submitted by the owner via the pre-meeting "Post an update" page.
/// Stored separately from vendor evidence and picked up by Q2FactAssembler on the next composition.
/// </summary>
public sealed record VendorUpdateNote(
    Guid           Id,
    Guid           VendorId,
    Guid?          VendorCallRunId,
    string         NoteText,
    string         SubmittedByUpn,
    DateTimeOffset SubmittedAtUtc);

public interface IVendorUpdateNoteStore
{
    Task SaveAsync(VendorUpdateNote note, CancellationToken ct);
    Task<IReadOnlyList<VendorUpdateNote>> GetForVendorAsync(Guid vendorId, CancellationToken ct);
}
