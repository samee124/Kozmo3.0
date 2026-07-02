namespace Wc.Contracts;

/// <summary>
/// Transport interface (§3). Simulated in Phase 3 (in-app pending list);
/// real email is a later swap — same interface, different implementation.
/// </summary>
public interface ICheckInTransport
{
    Task SendAsync(CheckIn checkIn, CancellationToken ct = default);
}
