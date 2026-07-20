using Wc.Contracts;

namespace Ii.Completeness;

using CheckIn = global::Wc.Contracts.CheckIn;

/// <summary>
/// raise_completeness_checkins stage (Phase 5, Commit 3).
/// Converts gap question IDs from a CompletenessProfile into durable DIMENSION_GAP check-ins
/// and writes them directly to ICheckInStore. Shares the DIMENSION_GAP CheckInKind with ad-hoc
/// gaps from Phase 3 (a distinct kind is a later observability refinement — spec §6).
///
/// Termination:
///   (a) Answered questions are never in GapQuestionIds — enforced by CompletenessRubric.
///   (b) permanentGapIds (caller-tracked questions that survived two consecutive cycles) are
///       skipped. Prevents the loop from re-asking unanswerable questions indefinitely.
///   (c) Depth-ladder cap is enforced upstream via QuestionSelector.Select(category, maxDepth).
///
/// Stores question.Id in CheckIn.TargetField so the convergence tracker can match
/// existing check-ins to their source question without string-matching question text.
/// Does NOT read the clock — caller supplies 'now'.
/// </summary>
public sealed class GapCheckInStage
{
    /// <summary>
    /// Raise DIMENSION_GAP check-ins for every gap question that is not permanent and not
    /// already pending for this vendor in the store. Returns the newly raised check-ins.
    /// </summary>
    /// <param name="transport">
    /// Optional — when supplied, each newly raised check-in is also sent through it (e.g. a real
    /// email). Defaults to null (no send), so every existing caller is unaffected; the in-app
    /// Pending queue read (ICheckInStore.GetOpenAsync) never depended on a transport firing.
    /// </param>
    public async Task<IReadOnlyList<CheckIn>> RaiseAsync(
        Guid                    vendorId,
        IReadOnlyList<string>   gapQuestionIds,
        IReadOnlyList<Question> allQuestions,
        IReadOnlySet<string>    permanentGapIds,
        ICheckInStore           checkInStore,
        string                  owner,
        Guid                    runId,
        DateTimeOffset          now,
        CancellationToken       ct = default,
        ICheckInTransport?      transport = null)
    {
        var byId = allQuestions.ToDictionary(q => q.Id, StringComparer.Ordinal);

        // De-dup: skip questions that already have an OPEN check-in for this vendor.
        // Prevents raising the same question again while a response is still pending.
        var openCheckIns = await checkInStore.GetOpenAsync(ct);
        var alreadyPending = openCheckIns
            .Where(c => c.VendorId == vendorId
                     && c.Kind == CheckInKind.DIMENSION_GAP
                     && c.TargetField != null)
            .Select(c => c.TargetField!)
            .ToHashSet(StringComparer.Ordinal);

        var raised = new List<CheckIn>();
        foreach (var id in gapQuestionIds)
        {
            if (permanentGapIds.Contains(id))     continue;  // termination (b): already tried
            if (alreadyPending.Contains(id))      continue;  // avoid duplicate while OPEN
            if (!byId.TryGetValue(id, out var q)) continue;  // unknown question (defensive)

            var checkIn = new CheckIn(
                CheckInId:      Guid.NewGuid(),
                VendorId:       vendorId,
                ProgramRunId:   runId,
                Kind:           CheckInKind.DIMENSION_GAP,
                Question:       q.Text,
                ResponseShape:  ToResponseShape(q.AnswerType),
                TargetField:    q.Id,   // question ID — used by convergence tracker to match
                Owner:          owner,
                Status:         PendingStatus.OPEN,
                RaisedAt:       now,
                AnsweredAt:     null,
                ExpiresAt:      null,
                ResponseValue:  null,
                PairedVendorId: null,
                TargetClaimKey: q.TargetClaimKey);  // E2 bridge — null for unbound questions

            await checkInStore.SaveAsync(checkIn, ct);
            raised.Add(checkIn);
        }

        // One digest send per RaiseAsync call — all newly raised check-ins for this vendor/run
        // in a single envelope. Fault-isolated: a transport failure must not stop check-ins from
        // being visible in-app; they are already persisted above regardless of send outcome.
        if (transport != null && raised.Count > 0)
        {
            try
            {
                await transport.SendAsync(raised.AsReadOnly(), ct);
            }
            catch (Exception)
            {
                // Send failed — every check-in already persisted and answerable in-app.
            }
        }

        return raised.AsReadOnly();
    }

    private static ResponseShape ToResponseShape(AnswerType t) => t switch
    {
        AnswerType.YesNo        => ResponseShape.YES_NO,
        AnswerType.TypedValue   => ResponseShape.TYPED_VALUE,
        AnswerType.StatusSelect => ResponseShape.STATUS_SELECT,
        _                       => ResponseShape.YES_NO,
    };
}
