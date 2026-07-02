namespace Km.Store;

/// <summary>
/// Storage interface for check-in rows (checkins table).
/// Uses primitive/BCL types only — no dependency on Wc.Contracts.
/// Implemented by SqliteEntityStore (shared connection, shared schema).
/// </summary>
public interface ICheckInRowStore
{
    Task SaveCheckInAsync(CheckInRow row, CancellationToken ct = default);
    Task<IReadOnlyList<CheckInRow>> GetOpenCheckInsAsync(CancellationToken ct = default);
    Task<CheckInRow?> GetCheckInAsync(Guid checkInId, CancellationToken ct = default);

    /// <summary>Returns all PROCESSED and EXPIRED check-ins for a vendor, ordered by raised_at.</summary>
    Task<IReadOnlyList<CheckInRow>> GetResolvedCheckInsForVendorAsync(Guid vendorId, CancellationToken ct = default);
}

/// <summary>Storage row for a check-in (checkins table).</summary>
public sealed record CheckInRow(
    Guid            CheckInId,
    Guid            VendorId,
    Guid            ProgramRunId,
    string          Kind,
    string          Question,
    string          ResponseShape,
    string?         TargetField,
    string          Owner,
    string          Status,
    DateTimeOffset  RaisedAt,
    DateTimeOffset? AnsweredAt,
    DateTimeOffset? ExpiresAt,
    string?         ResponseValue,
    Guid?           PairedVendorId = null
);
