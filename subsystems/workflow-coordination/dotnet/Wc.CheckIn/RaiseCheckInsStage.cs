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
///
/// Dedup: before inserting any row, the stage queries the store for existing OPEN check-ins.
/// A check-in is skipped if an OPEN row for the same (VendorId, TargetField) already exists
/// (for DIMENSION_GAP with a non-null TargetField) or the same (VendorId, Kind, Question)
/// already exists (for IDENTITY_CONFIRM and null-TargetField gaps). This prevents the same
/// gap from re-raising a new email on every pipeline rerun while a response is still pending.
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
        CancellationToken                    ct = default,
        ICheckInTransport?                   transport = null)
    {
        // Load open check-ins once — used for cross-run dedup in both paths below.
        var openCheckIns = await store.GetOpenAsync(ct);

        // Dedup sets keyed by natural identifiers for each kind.
        // IDENTITY_CONFIRM: no TargetField — dedup by (VendorId, Question text).
        var openIdentityByVendorQuestion = openCheckIns
            .Where(c => c.Kind == CheckInKind.IDENTITY_CONFIRM)
            .Select(c => (c.VendorId, c.Question))
            .ToHashSet();

        // DIMENSION_GAP with non-null TargetField: dedup by (VendorId, TargetField).
        var openGapByVendorTarget = openCheckIns
            .Where(c => c.Kind == CheckInKind.DIMENSION_GAP && c.TargetField != null)
            .Select(c => (c.VendorId, c.TargetField!))
            .ToHashSet();

        // DIMENSION_GAP with null TargetField (e.g. provisional-vendor STATUS_SELECT):
        // dedup by (VendorId, Question text).
        var openGapByVendorQuestion = openCheckIns
            .Where(c => c.Kind == CheckInKind.DIMENSION_GAP && c.TargetField == null)
            .Select(c => (c.VendorId, c.Question))
            .ToHashSet();

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

            // Cross-run dedup: skip if an OPEN IDENTITY_CONFIRM for this pair already exists.
            if (openIdentityByVendorQuestion.Contains((first.ClusterId, group.Key)))
                continue;

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
            // Cross-run dedup: skip if an OPEN check-in for the same slot already exists.
            if (g.TargetField != null &&
                openGapByVendorTarget.Contains((g.VendorId, g.TargetField)))
                continue;

            if (g.TargetField == null &&
                openGapByVendorQuestion.Contains((g.VendorId, g.Question)))
                continue;

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

        // One digest call per vendor — group raised check-ins by VendorId so each vendor
        // gets exactly one email envelope regardless of how many questions were raised.
        if (transport != null && raised.Count > 0)
        {
            foreach (var vendorGroup in raised.GroupBy(c => c.VendorId))
            {
                try
                {
                    await transport.SendAsync(vendorGroup.ToList(), ct);
                }
                catch (Exception)
                {
                    // Transport failure — check-ins already persisted and answerable in-app.
                }
            }
        }

        return raised;
    }
}
