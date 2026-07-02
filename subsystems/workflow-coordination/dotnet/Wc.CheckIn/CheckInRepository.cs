using Km.Store;
using Wc.Contracts;

namespace Wc.CheckIn;

using CheckIn = global::Wc.Contracts.CheckIn;

/// <summary>
/// Implements ICheckInStore wrapping ICheckInRowStore (primitive rows → domain records).
/// Mirrors the IdentityRegistry / IRegistryStore pattern: Km.Store holds the raw
/// persistence layer; Wc.CheckIn maps to/from domain types.
/// </summary>
public sealed class CheckInRepository : ICheckInStore
{
    private readonly ICheckInRowStore _store;

    public CheckInRepository(ICheckInRowStore store) => _store = store;

    public async Task SaveAsync(CheckIn checkIn, CancellationToken ct = default)
    {
        var row = new CheckInRow(
            CheckInId:      checkIn.CheckInId,
            VendorId:       checkIn.VendorId,
            ProgramRunId:   checkIn.ProgramRunId,
            Kind:           checkIn.Kind.ToString(),
            Question:       checkIn.Question,
            ResponseShape:  checkIn.ResponseShape.ToString(),
            TargetField:    checkIn.TargetField,
            Owner:          checkIn.Owner,
            Status:         checkIn.Status.ToString(),
            RaisedAt:       checkIn.RaisedAt,
            AnsweredAt:     checkIn.AnsweredAt,
            ExpiresAt:      checkIn.ExpiresAt,
            ResponseValue:  checkIn.ResponseValue,
            PairedVendorId: checkIn.PairedVendorId);
        await _store.SaveCheckInAsync(row, ct);
    }

    public async Task<IReadOnlyList<CheckIn>> GetOpenAsync(CancellationToken ct = default)
    {
        var rows = await _store.GetOpenCheckInsAsync(ct);
        return rows.Select(Map).ToList();
    }

    public async Task<CheckIn?> GetAsync(Guid checkInId, CancellationToken ct = default)
    {
        var row = await _store.GetCheckInAsync(checkInId, ct);
        return row is null ? null : Map(row);
    }

    private static CheckIn Map(CheckInRow row) => new CheckIn(
        CheckInId:      row.CheckInId,
        VendorId:       row.VendorId,
        ProgramRunId:   row.ProgramRunId,
        Kind:           Enum.Parse<CheckInKind>(row.Kind),
        Question:       row.Question,
        ResponseShape:  Enum.Parse<ResponseShape>(row.ResponseShape),
        TargetField:    row.TargetField,
        Owner:          row.Owner,
        Status:         Enum.Parse<PendingStatus>(row.Status),
        RaisedAt:       row.RaisedAt,
        AnsweredAt:     row.AnsweredAt,
        ExpiresAt:      row.ExpiresAt,
        ResponseValue:  row.ResponseValue,
        PairedVendorId: row.PairedVendorId);
}
