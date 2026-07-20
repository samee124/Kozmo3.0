using Wc.Contracts;

namespace Po.VendorCall;

/// <summary>
/// Guards against duplicate pre-meeting check-in dispatches for a given calendar event.
/// Uses the existing open-check-in store — no new storage table required.
/// </summary>
public static class VendorCallIdempotencyService
{
    /// <summary>
    /// Returns true when an OPEN DIMENSION_GAP check-in already exists for
    /// the given vendor and calendar event (identified by <paramref name="externalEventId"/>).
    /// </summary>
    public static async Task<bool> HasExistingPreMeetingCheckInAsync(
        Guid              vendorId,
        string            externalEventId,
        ICheckInStore     store,
        CancellationToken ct = default)
    {
        var targetField = TargetFieldFor(externalEventId);
        var open        = await store.GetOpenAsync(ct);

        return open.Any(c =>
            c.VendorId    == vendorId &&
            c.Kind        == CheckInKind.DIMENSION_GAP &&
            c.TargetField == targetField);
    }

    /// <summary>Canonical TargetField format for a vendor-call pre-meeting check-in.</summary>
    public static string TargetFieldFor(string externalEventId)
        => $"vendorcall_pre:{externalEventId}";
}
