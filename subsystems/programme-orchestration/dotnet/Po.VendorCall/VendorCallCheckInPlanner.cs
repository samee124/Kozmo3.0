using System.Security.Cryptography;
using System.Text;
using Wc.Contracts;

namespace Po.VendorCall;

/// <summary>
/// Raises a pre-meeting check-in through the existing Wc.CheckIn system.
///
/// Idempotent: if an OPEN DIMENSION_GAP check-in already exists for the meeting
/// (identified by TargetField = "vendorcall_pre:{externalEventId}"), no new check-in
/// is created and AlreadyDispatched is returned.
///
/// Transport failures are swallowed — check-ins are always persisted to ICheckInStore
/// regardless of email delivery outcome.
/// </summary>
public sealed class VendorCallCheckInPlanner
{
    private readonly string _ownerEmail;

    public VendorCallCheckInPlanner(string ownerEmail)
        => _ownerEmail = ownerEmail;

    /// <summary>
    /// Plans and raises the pre-meeting check-in for the given vendor call context.
    /// <paramref name="now"/> is passed in — this class never reads the clock.
    /// </summary>
    public async Task<CheckInDispatchResult> PlanPreMeetingAsync(
        VendorCallContext                  context,
        VendorCallEvidenceBundle           evidence,
        IReadOnlyList<VendorCallQuestion>  questionBank,
        ICheckInStore                      store,
        ICheckInTransport                  transport,
        DateTimeOffset                     now,
        CancellationToken                  ct = default)
    {
        // ── 1. Idempotency guard ───────────────────────────────────────────────
        if (await VendorCallIdempotencyService.HasExistingPreMeetingCheckInAsync(
                context.Match.VendorId, context.Meeting.ExternalId, store, ct))
            return new CheckInDispatchResult(CheckInDispatchStatus.AlreadyDispatched);

        // ── 2. Select question ────────────────────────────────────────────────
        var question = questionBank.FirstOrDefault(q =>
            string.Equals(q.Stage, "pre_meeting", StringComparison.OrdinalIgnoreCase));

        if (question is null)
            return new CheckInDispatchResult(CheckInDispatchStatus.NoQuestionsAvailable);

        // ── 3. Build check-in ─────────────────────────────────────────────────
        // ProgramRunId is derived deterministically from the meeting ExternalId so
        // that re-runs produce the same grouping key without needing a run registry.
        var programRunId = StableGuid(context.Meeting.ExternalId);
        var targetField  = VendorCallIdempotencyService.TargetFieldFor(context.Meeting.ExternalId);

        var checkIn = new CheckIn(
            CheckInId:     Guid.NewGuid(),
            VendorId:      context.Match.VendorId,
            ProgramRunId:  programRunId,
            Kind:          CheckInKind.DIMENSION_GAP,
            Question:      question.Prompt,
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

    /// <summary>Derives a deterministic Guid from a string (SHA-256 → first 16 bytes).</summary>
    private static Guid StableGuid(string input)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return new Guid(hash[..16]);
    }
}
