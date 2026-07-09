using Ig.Contracts;
using Ii.Contracts;
using Ii.Observation;
using Kozmo.Contracts;
using Kozmo.Contracts.Config;
using Km.Store;
using Wc.Contracts;

namespace Wc.CheckIn;

using CheckIn = global::Wc.Contracts.CheckIn;

/// <summary>
/// process_response stage (§4, Commit 3).
/// Matches an ANSWERED check-in by ID, converts the structured answer to evidence
/// (belief or identity merge), recomputes the affected vendor, and marks the
/// check-in PROCESSED.
///
/// Wrong-match guard (§5): the response applies ONLY to the vendor named in the
/// check-in. IDENTITY_CONFIRM applies ONLY to the specific pair stored in
/// PairedVendorId — never a different pair.
///
/// Does NOT read the clock — caller supplies 'now'.
/// check-in ≠ action: no ledger entry, no vendor action (§6).
/// </summary>
public sealed class ProcessCheckInService
{
    public async Task<ProcessResult> ProcessAsync(
        Guid                  checkInId,
        ICheckInStore         checkInStore,
        IIdentityRegistry     registry,
        VendorFileWriteService writeService,
        IIiFacade             facade,
        SaasProfile           profile,
        DateTimeOffset        now,
        CancellationToken     ct = default)
    {
        // ── §5 wrong-match guard ──────────────────────────────────────────────
        var checkIn = await checkInStore.GetAsync(checkInId, ct);
        if (checkIn is null)
            return new ProcessResult(ProcessOutcome.NotFound);
        if (checkIn.Status is PendingStatus.OPEN or PendingStatus.EXPIRED)
            return new ProcessResult(ProcessOutcome.NotAnswered);
        if (checkIn.Status == PendingStatus.PROCESSED)
            return new ProcessResult(ProcessOutcome.AlreadyProcessed);
        // status == ANSWERED — proceed

        var affectedVendorId = checkIn.VendorId;

        switch (checkIn.Kind)
        {
            case CheckInKind.IDENTITY_CONFIRM:
                await ProcessIdentityConfirmAsync(checkIn, registry, facade, now, ct);
                break;

            case CheckInKind.DIMENSION_GAP:
                await ProcessDimensionGapAsync(checkIn, writeService, profile, now, ct);
                await facade.RecomputeVendorAsync(affectedVendorId, ct);
                break;
        }

        // Stamp PROCESSED — idempotent guard for any retry
        var processed = checkIn with { Status = PendingStatus.PROCESSED };
        await checkInStore.SaveAsync(processed, ct);

        return new ProcessResult(ProcessOutcome.Ok, affectedVendorId);
    }

    // ── IDENTITY_CONFIRM ───────────────────────────────────────────────────────

    private static async Task ProcessIdentityConfirmAsync(
        CheckIn checkIn, IIdentityRegistry registry, IIiFacade facade,
        DateTimeOffset now, CancellationToken ct)
    {
        var isYes = string.Equals(checkIn.ResponseValue, "true", StringComparison.OrdinalIgnoreCase);
        if (isYes)
            await MergeVendorsAsync(checkIn, registry, facade, ct);
        else
            await LiftBothToConfirmedAsync(checkIn, registry, facade, ct);
    }

    private static async Task MergeVendorsAsync(
        CheckIn checkIn, IIdentityRegistry registry, IIiFacade facade, CancellationToken ct)
    {
        // Survivor = VendorId (first entity in pair); absorbed = PairedVendorId.
        var survivor = await registry.GetAsync(checkIn.VendorId, ct);
        if (survivor is null) return;

        var newAliases = survivor.Aliases.ToList();

        if (checkIn.PairedVendorId.HasValue)
        {
            var absorbed = await registry.GetAsync(checkIn.PairedVendorId.Value, ct);
            if (absorbed is not null)
            {
                // Fold all of the absorbed vendor's aliases into the survivor
                newAliases.AddRange(absorbed.Aliases);
                // Record the absorbed vendor's canonical name as an alias on the survivor,
                // cited to this check-in (provenance = checkin_id)
                newAliases.Add(new VendorAlias(
                    AliasId:         Guid.NewGuid(),
                    VendorId:        survivor.VendorId,
                    RawName:         absorbed.CanonicalName,
                    ProvenanceDocId: checkIn.CheckInId.ToString(),
                    ProvenanceSpan:  "identity-merge"));
            }
        }

        // Promote survivor to CONFIRMED with folded aliases
        var confirmedSurvivor = survivor with
        {
            Status  = RegistryStatus.Confirmed,
            Aliases = newAliases
        };
        await registry.SaveAsync(confirmedSurvivor, ct);

        // Non-destructive supersession: absorbed record is kept, pointing to survivor,
        // and excluded from the active vendor set (GetAllAsync skips Absorbed rows).
        if (checkIn.PairedVendorId.HasValue)
            await registry.MarkAbsorbedAsync(checkIn.PairedVendorId.Value, survivor.VendorId, ct);

        await facade.RecomputeVendorAsync(survivor.VendorId, ct);
    }

    private static async Task LiftBothToConfirmedAsync(
        CheckIn checkIn, IIdentityRegistry registry, IIiFacade facade, CancellationToken ct)
    {
        var v1 = await registry.GetAsync(checkIn.VendorId, ct);
        if (v1 is not null)
            await registry.SaveAsync(v1 with { Status = RegistryStatus.Confirmed }, ct);

        if (checkIn.PairedVendorId.HasValue)
        {
            var v2 = await registry.GetAsync(checkIn.PairedVendorId.Value, ct);
            if (v2 is not null)
                await registry.SaveAsync(v2 with { Status = RegistryStatus.Confirmed }, ct);
        }

        await facade.RecomputeVendorAsync(checkIn.VendorId, ct);
    }

    // ── DIMENSION_GAP ─────────────────────────────────────────────────────────

    private static async Task ProcessDimensionGapAsync(
        CheckIn checkIn, VendorFileWriteService writeService,
        SaasProfile profile, DateTimeOffset now, CancellationToken ct)
    {
        string claimKey;
        Dimension dimension;
        double rawValue;

        // E2 bridge: a question bound to a claim_key_catalogue key (Question.TargetClaimKey,
        // carried onto the check-in by GapCheckInStage) writes the REAL claim_key/dimension and
        // bands the actual answer through the SAME rubric function BeliefPersistenceStage uses
        // for extracted beliefs (ObservationModule.ScoreFromRubric) — so the belief is a real,
        // scored contribution to RubricModule, not just a present-field signal.
        if (TryResolveBoundBelief(checkIn, profile, out var boundClaimKey, out var boundDimension, out var bandedValue))
        {
            claimKey  = boundClaimKey;
            dimension = boundDimension;
            rawValue  = bandedValue;
        }
        else
        {
            // Unbound question, OR a bound question whose answer didn't band (out-of-domain /
            // unparseable) — exact prior behavior, unchanged. Resolve dimension from claim-key
            // catalogue by TargetField itself (default Financial when absent/unmatched); never
            // fabricate a score for an answer that couldn't be banded.
            dimension = Dimension.Financial;
            if (!string.IsNullOrEmpty(checkIn.TargetField)
                && profile.ClaimKeyCatalogue.TryGetValue(checkIn.TargetField, out var ckDef)
                && Enum.TryParse<Dimension>(ckDef.Dimension, ignoreCase: true, out var catalogueDim))
            {
                dimension = catalogueDim;
            }

            claimKey = checkIn.TargetField ?? "human_answer";
            rawValue = 1.0; // human confirmed the field → present/positive signal
        }

        // Tier = Confirmed (weight/ceiling 0.65) — deliberately ABOVE the 0.60 L1 critical
        // confidence gate (CompletenessRubric.Compute requires answer.Confidence >=
        // question.RequiredConfidence), unlike Reported (0.50), which can never clear it
        // (Invariant #4: REPORTED weight < CRITICAL gate — a DELIBERATE ceiling on unverified
        // third-party reports, not on a human operator directly confirming a specific question
        // through the check-in loop). Confirmed sits below Verified (0.80, a system fact) —
        // a human's direct answer is stronger evidence than someone else's report, weaker than
        // a system export. Provenance cites the check-in ID so the audit trail links back.
        //
        // ClaimKey and Criterion are BOTH set to the claim_key string (never the rubric_criterion
        // string) — the same convention BeliefPersistenceStage uses for extracted beliefs, so a
        // check-in-answered belief and a document/email-extracted belief for the same claim key
        // land in the identical (vendor, claim_key, dimension, criterion) supersession slot.
        //
        // Derivation names the actual question and the actual answer, not the generic
        // "vendor-file:{claimKey}" template — AnsweringPrompt.BeliefView never serializes Value
        // to the completeness LLM (only Dimension/Criterion/SourceTier/Confidence/Derivation), so
        // Derivation is the ONLY field carrying real semantic content back to completeness.
        await writeService.WriteBeliefAsync(
            vendorId:            checkIn.VendorId,
            claimKey:            claimKey,
            dimension:           dimension,
            criterion:           claimKey,
            rawValue:            rawValue,
            tier:                SourceTier.Confirmed,
            extractorConfidence: 0.65,
            observedAt:          now,
            provenance:          new BeliefProvenance(checkIn.CheckInId, $"check-in:{checkIn.Kind}"),
            ingestedAt:          now,
            derivation:          $"Check-in answer to \"{checkIn.Question}\": {checkIn.ResponseValue}",
            ct:                  ct);
    }

    // ── E2 bridge — resolve a bound question's answer into a real scored belief ────────────────

    private static bool TryResolveBoundBelief(
        CheckIn checkIn, SaasProfile profile,
        out string claimKey, out Dimension dimension, out double bandedValue)
    {
        claimKey = ""; dimension = Dimension.Financial; bandedValue = 0;

        if (string.IsNullOrEmpty(checkIn.TargetClaimKey)) return false;
        if (string.IsNullOrEmpty(checkIn.ResponseValue))  return false;
        if (!profile.ClaimKeyCatalogue.TryGetValue(checkIn.TargetClaimKey, out var ckDef)) return false;
        if (!Enum.TryParse<Dimension>(ckDef.Dimension, ignoreCase: true, out var dim))     return false;

        var banded = BandIfScored(profile, checkIn.TargetClaimKey, ckDef, checkIn.ResponseValue);
        if (banded is null) return false; // not scored, or the answer fell outside the rubric's
                                           // domain — abstain rather than fabricate; caller falls
                                           // back to the unbound present-field write.

        claimKey    = checkIn.TargetClaimKey;
        dimension   = dim;
        bandedValue = banded.Value;
        return true;
    }

    /// <summary>
    /// Mirrors Kyv.ProgramRunner.BeliefPersistenceStage.BandIfScored exactly — same catalogue
    /// field (RubricCriterion, falling back to the claim key itself), same scoring function.
    /// A structural claim key (ClaimClass != "scored") is not bindable — null, not the raw value —
    /// so TryResolveBoundBelief falls back to legacy behavior instead of persisting a magnitude
    /// under a rubric-scored shape it was never authored to carry.
    /// </summary>
    private static double? BandIfScored(SaasProfile profile, string claimKey, ClaimKeyDefinition ckDef, string rawResponseValue)
    {
        if (ckDef.ClaimClass != "scored") return null;
        var rubricCriterion = ckDef.RubricCriterion ?? claimKey;
        return ObservationModule.ScoreFromRubric(rubricCriterion, rawResponseValue, profile);
    }
}
