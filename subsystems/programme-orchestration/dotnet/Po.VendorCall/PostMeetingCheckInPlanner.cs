using System.Security.Cryptography;
using System.Text;
using Wc.Contracts;

namespace Po.VendorCall;

/// <summary>
/// Raises a post-meeting check-in through the existing Wc.CheckIn system.
///
/// Idempotent: if an OPEN DIMENSION_GAP check-in already exists for the meeting
/// (identified by TargetField = "vendorcall_post:{externalEventId}"), no new check-in
/// is created and AlreadyDispatched is returned.
///
/// Transport failures are swallowed — check-ins are always persisted to ICheckInStore
/// regardless of email delivery outcome.
///
/// The post-meeting question prompt may contain {vendorName} which is substituted
/// with the matched vendor's canonical name before the check-in is created.
/// </summary>
public sealed class PostMeetingCheckInPlanner
{
    private readonly string _ownerEmail;

    public PostMeetingCheckInPlanner(string ownerEmail) => _ownerEmail = ownerEmail;

    /// <summary>
    /// Plans and raises the post-meeting check-in for the given vendor call context.
    /// <paramref name="now"/> is passed in — this class never reads the clock.
    /// </summary>
    public async Task<CheckInDispatchResult> PlanPostMeetingAsync(
        VendorCallContext                  context,
        IReadOnlyList<VendorCallQuestion>  questionBank,
        ICheckInStore                      store,
        ICheckInTransport                  transport,
        DateTimeOffset                     now,
        CancellationToken                  ct = default)
    {
        // ── 1. Idempotency guard ───────────────────────────────────────────────
        if (await HasExistingPostMeetingCheckInAsync(
                context.Match.VendorId, context.Meeting.ExternalId, store, ct))
            return new CheckInDispatchResult(CheckInDispatchStatus.AlreadyDispatched);

        // ── 2. Select question ────────────────────────────────────────────────
        var question = questionBank.FirstOrDefault(q =>
            string.Equals(q.Stage, "post_meeting", StringComparison.OrdinalIgnoreCase));

        if (question is null)
            return new CheckInDispatchResult(CheckInDispatchStatus.NoQuestionsAvailable);

        // ── 3. Build check-in ─────────────────────────────────────────────────
        var programRunId = StableGuid(context.Meeting.ExternalId);
        var targetField  = PostMeetingTargetFieldFor(context.Meeting.ExternalId);

        // Substitute vendor name placeholder in the question prompt
        var prompt = question.Prompt.Replace(
            "{vendorName}", context.Match.VendorName, StringComparison.OrdinalIgnoreCase);

        var checkIn = new CheckIn(
            CheckInId:     Guid.NewGuid(),
            VendorId:      context.Match.VendorId,
            ProgramRunId:  programRunId,
            Kind:          CheckInKind.DIMENSION_GAP,
            Question:      prompt,
            ResponseShape: ResponseShape.STATUS_SELECT,
            TargetField:   targetField,
            Owner:         _ownerEmail,
            Status:        PendingStatus.OPEN,
            RaisedAt:      now,
            AnsweredAt:    null,
            ExpiresAt:     now.AddDays(question.ExpiryDays),
            ResponseValue: null);

        await store.SaveAsync(checkIn, ct);

        // ── 4. Attempt transport (non-fatal failure) ───────────────────────────
        try
        {
            await transport.SendAsync([checkIn], ct);
        }
        catch
        {
            // Transport failure is informational — the check-in is already persisted.
        }

        return new CheckInDispatchResult(CheckInDispatchStatus.Dispatched, checkIn);
    }

    /// <summary>Canonical TargetField format for a vendor-call post-meeting check-in.</summary>
    public static string PostMeetingTargetFieldFor(string externalEventId)
        => $"vendorcall_post:{externalEventId}";

    // ── Internal ───────────────────────────────────────────────────────────────

    private static async Task<bool> HasExistingPostMeetingCheckInAsync(
        Guid vendorId, string externalEventId, ICheckInStore store, CancellationToken ct)
    {
        var targetField = PostMeetingTargetFieldFor(externalEventId);
        var open        = await store.GetOpenAsync(ct);
        return open.Any(c =>
            c.VendorId    == vendorId &&
            c.Kind        == CheckInKind.DIMENSION_GAP &&
            c.TargetField == targetField);
    }

    private static Guid StableGuid(string input)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return new Guid(hash[..16]);
    }
}
