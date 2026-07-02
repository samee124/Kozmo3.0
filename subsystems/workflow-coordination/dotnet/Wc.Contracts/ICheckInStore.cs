namespace Wc.Contracts;

public interface ICheckInStore
{
    Task SaveAsync(CheckIn checkIn, CancellationToken ct = default);
    Task<IReadOnlyList<CheckIn>> GetOpenAsync(CancellationToken ct = default);
    Task<CheckIn?> GetAsync(Guid checkInId, CancellationToken ct = default);
}
