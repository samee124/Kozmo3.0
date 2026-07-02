using Wc.Contracts;

namespace Wc.CheckIn;

using CheckIn = global::Wc.Contracts.CheckIn;

/// <summary>
/// Simulated transport: "sending" = the check-in is already persisted OPEN by the raise stage.
/// The in-app pending view reads directly from ICheckInStore.GetOpenAsync — no push step needed.
/// A real-email implementation swaps in here with no changes to the loop or processing code.
/// </summary>
public sealed class InAppCheckInTransport : ICheckInTransport
{
    public Task SendAsync(CheckIn checkIn, CancellationToken ct = default)
        => Task.CompletedTask;
}
