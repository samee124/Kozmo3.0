using Ig.Contracts;
using Ii.Contracts;
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
        // Resolve dimension from claim-key catalogue; default to Financial when absent
        var dimension = Dimension.Financial;
        if (!string.IsNullOrEmpty(checkIn.TargetField)
            && profile.ClaimKeyCatalogue.TryGetValue(checkIn.TargetField, out var ckDef)
            && Enum.TryParse<Dimension>(ckDef.Dimension, ignoreCase: true, out var catalogueDim))
        {
            dimension = catalogueDim;
        }

        var claimKey = checkIn.TargetField ?? "human_answer";

        // Human confirmed the field → rawValue = 1.0 (present/positive signal).
        // Tier = Confirmed (weight/ceiling 0.65) — deliberately ABOVE the 0.60 L1 critical
        // confidence gate (CompletenessRubric.Compute requires answer.Confidence >=
        // question.RequiredConfidence), unlike Reported (0.50), which can never clear it
        // (Invariant #4: REPORTED weight < CRITICAL gate — a DELIBERATE ceiling on unverified
        // third-party reports, not on a human operator directly confirming a specific question
        // through the check-in loop). Confirmed sits below Verified (0.80, a system fact) —
        // a human's direct answer is stronger evidence than someone else's report, weaker than
        // a system export. Provenance cites the check-in ID so the audit trail links back.
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
            rawValue:            1.0,
            tier:                SourceTier.Confirmed,
            extractorConfidence: 0.65,
            observedAt:          now,
            provenance:          new BeliefProvenance(checkIn.CheckInId, $"check-in:{checkIn.Kind}"),
            ingestedAt:          now,
            derivation:          $"Check-in answer to \"{checkIn.Question}\": {checkIn.ResponseValue}",
            ct:                  ct);
    }
}
