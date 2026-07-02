using Ig.Contracts;
using Wc.Contracts;

namespace Wc.CheckIn;

using CheckIn = global::Wc.Contracts.CheckIn;

/// <summary>
/// raise_checkins stage (§0, §1, Commit 1).
/// Converts TRIAGE dispositions + gap requests into durable OPEN CheckIn records.
/// The question is COPIED from the disposition/gap — never re-derived here.
/// No transport (Commit 2), no response processing (Commit 3).
/// Does NOT read the clock — caller supplies 'now'.
/// </summary>
public sealed class RaiseCheckInsStage
{
    public async Task<IReadOnlyList<CheckIn>> RaiseAsync(
        IReadOnlyList<ResolutionDisposition> dispositions,
        IReadOnlyList<VendorGapRequest>      gapRequests,
        ICheckInStore                        store,
        string                               owner,
        Guid                                 programRunId,
        DateTimeOffset                       now,
        CancellationToken                    ct = default)
    {
        var raised = new List<CheckIn>();

        // ── IDENTITY_CONFIRM: TRIAGE + PossibleSameEntity → YES_NO ─────────────
        // A near-miss pair creates two dispositions with identical TriageQuestion.
        // Group by question so ONE check-in is raised per pair, with PairedVendorId
        // capturing the second entity so process_response (Commit 3) knows both sides.
        var triageGroups = dispositions
            .Where(d => d.Disposition == Disposition.Triage
                     && d.Flags.Contains(ResolutionFlags.PossibleSameEntity)
                     && !string.IsNullOrEmpty(d.TriageQuestion))
            .GroupBy(d => d.TriageQuestion!, StringComparer.Ordinal)
            .ToList();

        foreach (var group in triageGroups)
        {
            var first  = group.First();
            var second = group.Count() > 1 ? (Guid?)group.ElementAt(1).ClusterId : null;

            var checkIn = new CheckIn(
                CheckInId:      Guid.NewGuid(),
                VendorId:       first.ClusterId,
                ProgramRunId:   programRunId,
                Kind:           CheckInKind.IDENTITY_CONFIRM,
                Question:       first.TriageQuestion!,
                ResponseShape:  ResponseShape.YES_NO,
                TargetField:    null,
                Owner:          owner,
                Status:         PendingStatus.OPEN,
                RaisedAt:       now,
                AnsweredAt:     null,
                ExpiresAt:      null,
                ResponseValue:  null,
                PairedVendorId: second);

            await store.SaveAsync(checkIn, ct);
            raised.Add(checkIn);
        }

        // ── DIMENSION_GAP: caller-formed gap request → structured ask ───────────
        foreach (var g in gapRequests)
        {
            var checkIn = new CheckIn(
                CheckInId:      Guid.NewGuid(),
                VendorId:       g.VendorId,
                ProgramRunId:   programRunId,
                Kind:           CheckInKind.DIMENSION_GAP,
                Question:       g.Question,
                ResponseShape:  g.ResponseShape,
                TargetField:    g.TargetField,
                Owner:          owner,
                Status:         PendingStatus.OPEN,
                RaisedAt:       now,
                AnsweredAt:     null,
                ExpiresAt:      null,
                ResponseValue:  null,
                PairedVendorId: null);

            await store.SaveAsync(checkIn, ct);
            raised.Add(checkIn);
        }

        return raised;
    }
}
