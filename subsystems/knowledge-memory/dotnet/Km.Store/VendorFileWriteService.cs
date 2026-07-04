using Kozmo.Contracts;
using Kozmo.Contracts.Config;
using Kozmo.Contracts.Interfaces;

namespace Km.Store;

/// <summary>
/// Vendor file write path (§7 of spec).
/// Writes beliefs extracted from evidence documents. The store enforces §2 tier ceilings
/// and §7 supersession; this service handles claim_key resolution, half-life lookup,
/// structural zero, and version sequencing.
/// </summary>
public sealed class VendorFileWriteService
{
    private readonly IEntityStore _store;
    private readonly SaasProfile  _profile;

    public VendorFileWriteService(IEntityStore store, SaasProfile profile)
    {
        _store   = store;
        _profile = profile;
    }

    /// <summary>
    /// Write a vendor file belief, applying the tier ceiling cap and supersession rules.
    /// Returns the written belief.
    /// </summary>
    /// <param name="derivation">
    /// The real evidence text (e.g. a quoted span: doc:QBR.pdf "4.6 out of 5.0"), when the
    /// caller has one. Falls back to the generic "vendor-file:{claimKey}" template when null or
    /// blank — preserves exact prior behavior for every caller that doesn't supply one.
    /// Derivation is annotation only — excluded from the belief fingerprint (FingerprintComputer
    /// hashes only Dimension/Criterion/Value/Confidence) — so this never affects scoring or
    /// determinism.
    /// </param>
    public async Task<Belief> WriteBeliefAsync(
        Guid           vendorId,
        string         claimKey,
        Dimension      dimension,
        string         criterion,
        double         rawValue,
        SourceTier     tier,
        double         extractorConfidence,
        DateTimeOffset observedAt,
        BeliefProvenance provenance,
        DateTimeOffset ingestedAt,
        DateTimeOffset? validUntil = null,
        string?        derivation = null,
        CancellationToken ct = default)
    {
        // Resolve claim_key definition once — used for ceiling and half-life
        _profile.ClaimKeyCatalogue.TryGetValue(claimKey, out var ckDef);

        // Structural claims carry Confidence=0 so they never feed RubricModule (which filters on
        // Confidence > 0, not Dimension — see below). Non-structural: pass extractorConfidence
        // through; the store applies §2 ceiling on write.
        var isStructural = ckDef?.ClaimClass == "structural";
        var confidence   = isStructural ? 0.0 : extractorConfidence;

        // Dimension nulling used to be blanket "isStructural -> null", which also nulled
        // annual_value/payment_terms even though the catalogue tags both Financial — making real
        // Financial facts invisible to every dimension-grouped view (trail, vendor-file dimension
        // counts) even though they could never leak into scoring anyway (Confidence=0 already
        // excludes them there). Now: only claims the catalogue itself declares dimensionless
        // (dimension: "" — renewal_date, notice_period, auto_renewal, liability_cap,
        // contract_on_file) carry a null Dimension. A structural claim WITH a catalogued
        // dimension (annual_value, payment_terms -> Financial) keeps it — safe for scoring
        // because RubricModule.ScoreDimension filters on Confidence > 0, not Dimension, and
        // Confidence stays forced to 0 above regardless of this change.
        var hasCatalogueDimension = !string.IsNullOrEmpty(ckDef?.Dimension);
        var effectiveDim = hasCatalogueDimension
            ? (Dimension?)dimension
            : isStructural ? (Dimension?)null : (Dimension?)dimension;

        // Half-life from claim_key catalogue; null means use tier-based decay
        var halfLifeDays = ckDef != null ? (ckDef.HalfLifeDays ?? 0) : (int?)null;

        // Supersession: find prior active belief in same (vendor, claim_key) slot
        var allCurrent = await _store.GetCurrentBeliefsAsync(vendorId, ct);
        var prior = allCurrent.FirstOrDefault(b => b.ClaimKey == claimKey
                                                 && b.Dimension == effectiveDim
                                                 && b.Criterion == criterion);

        var version = prior?.Version + 1 ?? 1;

        var belief = new Belief(
            Id:            Guid.NewGuid(),
            EntityId:      vendorId,
            Dimension:     effectiveDim,
            Criterion:     criterion,
            Value:         rawValue,
            SourceTier:    tier,
            Confidence:    confidence,   // structural=0.0; scored=extractorConfidence (store clamps to §2 ceiling)
            Freshness:     1.0,          // decay applied at scoring time via DecayEngine
            Derivation:    string.IsNullOrWhiteSpace(derivation) ? $"vendor-file:{claimKey}" : derivation,
            SourceSignals: [],
            Version:       version,
            SupersededBy:  null,
            CreatedAt:     ingestedAt,
            TraceId:       Guid.NewGuid())
        {
            ClaimKey     = claimKey,
            ObservedAt   = observedAt,
            HalfLifeDays = halfLifeDays,
            ValidUntil   = validUntil,
            Provenance   = provenance,
            ClassificationMethod = ClassificationMethod.Rule
        };

        // §2 ceiling clamping and §7 supersession are enforced by the store.
        await _store.AppendBeliefAsync(belief, ct);

        // Return the persisted belief so the caller sees the store-enforced confidence.
        var current = await _store.GetCurrentBeliefsAsync(vendorId, ct);
        return current.FirstOrDefault(b => b.Id == belief.Id) ?? belief;
    }
}
