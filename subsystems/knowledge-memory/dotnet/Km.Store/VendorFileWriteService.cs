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
        CancellationToken ct = default)
    {
        // Resolve claim_key definition once — used for ceiling and half-life
        _profile.ClaimKeyCatalogue.TryGetValue(claimKey, out var ckDef);

        // Structural claims carry null dimension and Confidence=0 so they don't feed RubricModule.
        // Non-structural: pass extractorConfidence through; the store applies §2 ceiling on write.
        var isStructural = ckDef?.ClaimClass == "structural";
        var confidence   = isStructural ? 0.0 : extractorConfidence;
        var effectiveDim = isStructural ? (Dimension?)null : (Dimension?)dimension;

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
            Derivation:    $"vendor-file:{claimKey}",
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
