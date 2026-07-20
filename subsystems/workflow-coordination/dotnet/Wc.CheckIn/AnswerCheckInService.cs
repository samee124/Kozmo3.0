using Ig.Contracts;
using Ii.Contracts;
using Kozmo.Contracts;
using Kozmo.Contracts.Config;
using Km.Store;
using Wc.Contracts;

namespace Wc.CheckIn;

using CheckIn = global::Wc.Contracts.CheckIn;

/// <summary>
/// Records a structured human response against an OPEN check-in.
/// Validates existence, open state, and shape compatibility; then stamps ANSWERED.
/// Does NOT process the response further — that is Commit 3 (process_response stage).
/// Does NOT read the clock — caller supplies 'now'.
/// </summary>
public sealed class AnswerCheckInService
{
    public async Task<AnswerResult> AnswerAsync(
        Guid           checkInId,
        string         responseValue,
        DateTimeOffset now,
        ICheckInStore  store,
        CancellationToken ct = default)
    {
        var checkIn = await store.GetAsync(checkInId, ct);
        if (checkIn is null)
            return new AnswerResult(AnswerOutcome.NotFound);

        if (checkIn.Status != PendingStatus.OPEN)
            return new AnswerResult(AnswerOutcome.AlreadyAnswered);

        if (!IsValid(checkIn.ResponseShape, responseValue))
            return new AnswerResult(AnswerOutcome.ShapeMismatch);

        var updated = checkIn with
        {
            Status        = PendingStatus.ANSWERED,
            AnsweredAt    = now,
            ResponseValue = responseValue
        };
        await store.SaveAsync(updated, ct);
        return new AnswerResult(AnswerOutcome.Ok, updated);
    }

    // ── Combined answer + process ──────────────────────────────────────────────

    /// <summary>
    /// Combined answer-and-process: validates, records the answer, writes a belief
    /// (for DIMENSION_GAP), triggers recompute, and stamps PROCESSED in a single call.
    ///
    /// UNKNOWN (null or "UNKNOWN" responseValue): stamps PROCESSED with no belief write
    /// and no recompute — the check-in is closed without influencing any score.
    ///
    /// Belief tier is SourceTier.Reported (weight 0.50). For YES_NO, the raw score is
    /// a sentinel: "true" → 1.0, "false" → 0.0. For TYPED_VALUE / STATUS_SELECT the
    /// value is banded via the scoring_rubric criterion; if the value is out of domain,
    /// the belief write is skipped (check-in is still stamped PROCESSED).
    ///
    /// Does NOT read the clock — caller supplies 'now'.
    /// </summary>
    public async Task<AnswerResult> ProcessAnswerAsync(
        Guid                   checkInId,
        string?                responseValue,
        DateTimeOffset         now,
        ICheckInStore          checkInStore,
        VendorFileWriteService writeService,
        SaasProfile            profile,
        IIiFacade              facade,
        IIdentityRegistry      registry,
        string?                answeredBy = null,  // optional — when provided, captured in belief provenance
        CancellationToken      ct = default)
    {
        var checkIn = await checkInStore.GetAsync(checkInId, ct);
        if (checkIn is null)
            return new AnswerResult(AnswerOutcome.NotFound);

        if (checkIn.Status != PendingStatus.OPEN)
            return new AnswerResult(AnswerOutcome.AlreadyAnswered);

        // UNKNOWN — close with no belief write, no recompute.
        if (string.IsNullOrWhiteSpace(responseValue) ||
            responseValue.Equals("UNKNOWN", StringComparison.OrdinalIgnoreCase))
        {
            var processed = checkIn with { Status = PendingStatus.PROCESSED, AnsweredAt = now };
            await checkInStore.SaveAsync(processed, ct);
            return new AnswerResult(AnswerOutcome.Ok, processed);
        }

        if (!IsValid(checkIn.ResponseShape, responseValue))
            return new AnswerResult(AnswerOutcome.ShapeMismatch);

        switch (checkIn.Kind)
        {
            case CheckInKind.DIMENSION_GAP:
            {
                // Write belief BEFORE stamping status — so a write failure leaves the
                // check-in OPEN and retryable (status stays OPEN if this throws).
                var phantom = checkIn with { AnsweredAt = now, ResponseValue = responseValue };
                await WriteDimensionGapBeliefAsync(phantom, writeService, profile, now, answeredBy, ct);
                await facade.RecomputeVendorAsync(checkIn.VendorId, ct);
                var done = checkIn with
                {
                    Status        = PendingStatus.PROCESSED,
                    AnsweredAt    = now,
                    ResponseValue = responseValue
                };
                await checkInStore.SaveAsync(done, ct);
                return new AnswerResult(AnswerOutcome.Ok, done);
            }

            case CheckInKind.IDENTITY_CONFIRM:
            {
                // ORDERING CONSTRAINT (pre-existing, not introduced here):
                // ProcessCheckInService.ProcessAsync requires the check-in already in ANSWERED
                // state — it returns NotAnswered for OPEN check-ins. We must stamp ANSWERED
                // before the merge, not after. If ProcessAsync throws mid-merge, the check-in
                // lands in ANSWERED (not OPEN, not PROCESSED). That is retryable: a direct
                // call to ProcessCheckInService.ProcessAsync will pick up the ANSWERED check-in
                // and complete the merge. This stamp-before-merge ordering existed in the old
                // endpoint code before consolidation — fixing it requires changing
                // ProcessCheckInService, which is out of scope.
                var answered = checkIn with
                {
                    Status        = PendingStatus.ANSWERED,
                    AnsweredAt    = now,
                    ResponseValue = responseValue
                };
                await checkInStore.SaveAsync(answered, ct);
                var processSvc = new ProcessCheckInService();
                await processSvc.ProcessAsync(
                    checkInId, checkInStore, registry, writeService, facade, profile, now, ct);
                var done = await checkInStore.GetAsync(checkInId, ct) ?? answered;
                return new AnswerResult(AnswerOutcome.Ok, done);
            }

            default:
            {
                var done = checkIn with
                {
                    Status        = PendingStatus.PROCESSED,
                    AnsweredAt    = now,
                    ResponseValue = responseValue
                };
                await checkInStore.SaveAsync(done, ct);
                return new AnswerResult(AnswerOutcome.Ok, done);
            }
        }
    }

    // ── Belief write helpers ───────────────────────────────────────────────────

    private static async Task WriteDimensionGapBeliefAsync(
        CheckIn                checkIn,
        VendorFileWriteService writeService,
        SaasProfile            profile,
        DateTimeOffset         now,
        string?                answeredBy,
        CancellationToken      ct)
    {
        var claimKey  = checkIn.TargetField ?? "human_answer";
        var dimension = ResolveDimension(claimKey, profile);
        var criterion = ResolveRubricCriterion(claimKey, profile);
        var rawScore  = ComputeScore(checkIn.ResponseShape, checkIn.ResponseValue!, criterion, profile);

        // If the value is out of rubric domain or unscoreable, skip the belief write
        // but still stamp PROCESSED (the check-in is answered, just not evidence-mapped).
        if (rawScore is null) return;

        var provenanceLocator = answeredBy is not null
            ? $"check-in:{checkIn.Kind}|answered-by:{answeredBy}"
            : $"check-in:{checkIn.Kind}";

        await writeService.WriteBeliefAsync(
            vendorId:            checkIn.VendorId,
            claimKey:            claimKey,
            dimension:           dimension,
            criterion:           criterion,
            rawValue:            rawScore.Value,
            tier:                SourceTier.Reported,
            extractorConfidence: 0.50,
            observedAt:          now,
            provenance:          new BeliefProvenance(checkIn.CheckInId, provenanceLocator),
            ingestedAt:          now,
            derivation:          $"Check-in answer to \"{checkIn.Question}\": {checkIn.ResponseValue}",
            ct:                  ct);
    }

    private static Dimension ResolveDimension(string claimKey, SaasProfile profile)
    {
        if (profile.ClaimKeyCatalogue.TryGetValue(claimKey, out var ckDef)
            && Enum.TryParse<Dimension>(ckDef.Dimension, ignoreCase: true, out var dim))
            return dim;
        return Dimension.Financial;
    }

    private static string ResolveRubricCriterion(string claimKey, SaasProfile profile)
    {
        if (profile.ClaimKeyCatalogue.TryGetValue(claimKey, out var ckDef)
            && !string.IsNullOrEmpty(ckDef.RubricCriterion))
            return ckDef.RubricCriterion;
        return claimKey;
    }

    /// <summary>
    /// Maps a raw response to a 0–1 rubric score.
    /// YES_NO: "true" → 1.0, "false" → 0.0 (sentinel — no rubric lookup needed).
    /// TYPED_VALUE / STATUS_SELECT: banded via scoring_rubric; null if out of domain.
    /// </summary>
    private static double? ComputeScore(
        ResponseShape shape, string responseValue, string criterion, SaasProfile profile)
    {
        switch (shape)
        {
            case ResponseShape.YES_NO:
                if (responseValue.Equals("true",  StringComparison.OrdinalIgnoreCase)) return 1.0;
                if (responseValue.Equals("false", StringComparison.OrdinalIgnoreCase)) return 0.0;
                return null;

            case ResponseShape.TYPED_VALUE:
            case ResponseShape.STATUS_SELECT:
                return BandViaRubric(criterion, responseValue, profile);

            default:
                return null;
        }
    }

    /// <summary>
    /// Bands a raw value against the scoring_rubric criterion.
    /// Mirrors ObservationModule.ScoreFromRubric without the Ii.Observation dependency.
    /// Returns null when the criterion is unknown or the value is outside the rubric domain.
    /// </summary>
    private static double? BandViaRubric(string criterion, string rawValue, SaasProfile profile)
    {
        if (!profile.ScoringRubric.TryGetValue(criterion, out var rubric)) return null;

        if (rubric.Type == "enum")
        {
            return rubric.EnumScores != null
                && rubric.EnumScores.TryGetValue(rawValue, out var es) ? es : null;
        }

        if (rubric.Type == "numeric" && rubric.NumericThresholds != null)
        {
            if (!double.TryParse(rawValue,
                    System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var num))
                return null;

            var domainMin = rubric.NumericThresholds.Min(t => t.Min);
            var domainMax = rubric.NumericThresholds.Max(t => t.Max);
            if (num < domainMin || num > domainMax) return null;

            foreach (var t in rubric.NumericThresholds)
            {
                var isTopBucket = t.Max == domainMax;
                var inRange = num >= t.Min && (num < t.Max || (isTopBucket && num <= t.Max));
                if (inRange) return t.Score;
            }
        }

        return null;
    }

    // ── Shape validator (shared by AnswerAsync and ProcessAnswerAsync) ─────────

    private static bool IsValid(ResponseShape shape, string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        return shape switch
        {
            ResponseShape.YES_NO => value.Equals("true",  StringComparison.OrdinalIgnoreCase) ||
                                    value.Equals("false", StringComparison.OrdinalIgnoreCase),
            ResponseShape.TYPED_VALUE   => true,
            ResponseShape.STATUS_SELECT => true,
            _                           => false
        };
    }
}
