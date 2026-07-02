using Ig.Contracts;
using Kozmo.Contracts;

// §0 REUSE: this is the gate PATTERN reused with identity-specific thresholds.
// Structural shape mirrors IndexModule.AssignBand (threshold comparison → bucketed output):
//   if (triage-forced)                         → Triage
//   if (confidence ≥ config.AutoConfirmMin
//       AND cluster is "strong")               → AutoConfirm
//   else                                       → Provisional
// AUTO_CONFIRM_MIN comes from IdentityGateConfig (config object), not a magic number.
// AssignBand is NOT called (wrong output type; wrong domain).
// Tier-rank is NOT reimplemented (not needed — SourceTier.Primary is compared by enum
// identity, not by a reimplemented rank function).

namespace Ig.Resolution;

/// <summary>
/// Stage E — Gate 1: disposition.
/// Produces one ResolutionDisposition per annotated cluster.
/// Triage dispositions carry a fully-formed triage_question ready for Phase 3 to send.
/// </summary>
public sealed class IdentityGate
{
    private readonly IdentityGateConfig _config;

    public IdentityGate(IdentityGateConfig? config = null)
        => _config = config ?? IdentityGateConfig.Default;

    public IReadOnlyList<ResolutionDisposition> Assign(
        IReadOnlyList<CandidateCluster> clusters)
        => clusters.Select(c => BuildDisposition(c, clusters)).ToList();

    // ── Gate logic (same structural pattern as AssignBand) ─────────────────────

    private ResolutionDisposition BuildDisposition(
        CandidateCluster            cluster,
        IReadOnlyList<CandidateCluster> all)
    {
        var memberIds = cluster.Members
            .Select(m => m.Normalized.Candidate.CandidateId)
            .ToList();

        // ── NON-VENDOR ROLE DROP: customer/issuer/internal → never a confirmed vendor ─
        if (IsNonVendorRole(cluster.EntityRole))
        {
            var nonVendorFlags = AddFlag(cluster.Flags, ResolutionFlags.NonVendorEntity);

            return new ResolutionDisposition(
                ClusterId:             cluster.ClusterId,
                MemberCandidateIds:    memberIds,
                ProposedCanonicalName: cluster.CanonicalName,
                ComparisonKey:         cluster.ComparisonKey,
                EntityType:            cluster.EntityType,
                Disposition:           Disposition.NonVendor,
                Confidence:            cluster.Confidence,
                Flags:                 nonVendorFlags,
                TriageReason:          null,
                TriageQuestion:        null);
        }

        // ── TRIAGE: blocking flags or ambiguous entity type ────────────────────
        if (IsTriageForced(cluster, all))
        {
            var (reason, question) = BuildTriageContext(cluster, all);
            var triageFlags = AddFlag(
                AddFlag(cluster.Flags, reason),
                ResolutionFlags.TriageRequired);

            return new ResolutionDisposition(
                ClusterId:             cluster.ClusterId,
                MemberCandidateIds:    memberIds,
                ProposedCanonicalName: cluster.CanonicalName,
                ComparisonKey:         cluster.ComparisonKey,
                EntityType:            cluster.EntityType,
                Disposition:           Disposition.Triage,
                Confidence:            cluster.Confidence,
                Flags:                 triageFlags,
                TriageReason:          reason,
                TriageQuestion:        question);
        }

        // ── AUTO_CONFIRM or PROVISIONAL ────────────────────────────────────────
        bool strong   = IsStrongCluster(cluster);
        bool aboveMin = cluster.Confidence >= _config.AutoConfirmMin;

        var disposition = (strong && aboveMin)
            ? Disposition.AutoConfirm
            : Disposition.Provisional;

        var flags = disposition == Disposition.AutoConfirm
            ? AddFlag(cluster.Flags, ResolutionFlags.AutoConfirmed)
            : AddFlag(cluster.Flags, ResolutionFlags.ProvisionalVendor);

        return new ResolutionDisposition(
            ClusterId:             cluster.ClusterId,
            MemberCandidateIds:    memberIds,
            ProposedCanonicalName: cluster.CanonicalName,
            ComparisonKey:         cluster.ComparisonKey,
            EntityType:            cluster.EntityType,
            Disposition:           disposition,
            Confidence:            cluster.Confidence,
            Flags:                 flags,
            TriageReason:          null,
            TriageQuestion:        null);
    }

    // ── Predicates ─────────────────────────────────────────────────────────────

    private static bool IsNonVendorRole(string entityRole) =>
        entityRole is "customer" or "issuer" or "internal";

    private static bool IsTriageForced(CandidateCluster c, IReadOnlyList<CandidateCluster> all)
    {
        if (c.Flags.Contains(ResolutionFlags.Collision))       return true;
        if (c.Flags.Contains(ResolutionFlags.SuspectedRebrand)) return true;
        if (c.EntityType == EntityType.Unknown)                return true;

        // Near-miss: only triage if the similar-named partner is also a vendor candidate.
        // If the partner is non-vendor (customer/etc.), the near-miss question is moot.
        if (c.Flags.Contains(ResolutionFlags.PossibleSameEntity))
        {
            var partner = FindNearMissPartner(c, all);
            return partner != null && !IsNonVendorRole(partner.EntityRole);
        }

        return false;
    }

    /// <summary>
    /// "Strong" = multi-member cluster (variants corroborate each other) OR a single
    /// candidate whose source is PRIMARY (signed contract — tier ceiling 1.0, no network needed).
    /// </summary>
    private static bool IsStrongCluster(CandidateCluster c)
    {
        if (c.Members.Count > 1) return true;
        return c.Members[0].Normalized.Candidate.SourceTier == SourceTier.Primary;
    }

    // ── Triage question builder (§5: fully-formed for Phase 3) ─────────────────

    private static (string reason, string question) BuildTriageContext(
        CandidateCluster            cluster,
        IReadOnlyList<CandidateCluster> all)
    {
        if (cluster.Flags.Contains(ResolutionFlags.Collision))
        {
            // Find the paired collision cluster (same key, conflicting signals)
            var partner = all.FirstOrDefault(c =>
                c.ClusterId != cluster.ClusterId &&
                string.Equals(c.ComparisonKey, cluster.ComparisonKey, StringComparison.Ordinal) &&
                c.Flags.Contains(ResolutionFlags.Collision));

            var mySignal      = GetPrimarySignal(cluster) ?? "unknown signal";
            var partnerSignal = partner != null ? (GetPrimarySignal(partner) ?? "unknown signal") : "unknown signal";
            var partnerName   = partner?.CanonicalName ?? cluster.CanonicalName;

            var question =
                $"Two vendors share the name '{cluster.CanonicalName}' but have conflicting signals " +
                $"('{mySignal}' vs '{partnerSignal}'). " +
                $"Are '{cluster.CanonicalName}' ({mySignal}) and '{partnerName}' ({partnerSignal}) " +
                $"the same vendor or two distinct companies?";

            return (ResolutionFlags.Collision, question);
        }

        if (cluster.Flags.Contains(ResolutionFlags.SuspectedRebrand))
        {
            // Find the paired rebrand cluster (different key, matching signal)
            var partner = all.FirstOrDefault(c =>
                c.ClusterId != cluster.ClusterId &&
                c.Flags.Contains(ResolutionFlags.SuspectedRebrand) &&
                SharesAnySignal(cluster, c));

            var sharedSignal = partner != null ? GetSharedSignal(cluster, partner) : null;
            var partnerName  = partner?.CanonicalName ?? "unknown vendor";

            var question =
                $"Vendor '{cluster.CanonicalName}' and vendor '{partnerName}'" +
                (sharedSignal != null
                    ? $" share the signal '{sharedSignal}'"
                    : " share corroborating signals") +
                $" but have different names. " +
                $"Did one rebrand or acquire the other? " +
                $"If yes, link '{cluster.CanonicalName}' and '{partnerName}'; " +
                $"if no, confirm they are distinct vendors.";

            return (ResolutionFlags.SuspectedRebrand, question);
        }

        if (cluster.Flags.Contains(ResolutionFlags.PossibleSameEntity))
        {
            var partner     = FindNearMissPartner(cluster, all);
            var partnerName = partner?.CanonicalName ?? "unknown vendor";

            // Sort names so both sides of the pair produce the same question string —
            // RaiseCheckInsStage groups by question to dedup the pair into one check-in
            // with PairedVendorId set; asymmetric ordering would create two orphan check-ins.
            var (nameA, nameB) = string.Compare(
                cluster.CanonicalName, partnerName, StringComparison.Ordinal) <= 0
                ? (cluster.CanonicalName, partnerName)
                : (partnerName, cluster.CanonicalName);

            var nearMissQuestion =
                $"Are '{nameA}' and '{nameB}' the same vendor? " +
                $"Their names are similar but distinct (they were not automatically merged). " +
                $"Confirm whether to merge or treat as separate vendors.";

            return (ResolutionFlags.PossibleSameEntity, nearMissQuestion);
        }

        // Unknown entity type
        var unknownQuestion =
            $"The entity type of '{cluster.CanonicalName}' (key: '{cluster.ComparisonKey}') " +
            $"could not be determined by rule. " +
            $"Please confirm: is this a company, a person, or a product?";

        return ("ambiguous-entity-type", unknownQuestion);
    }

    // ── Near-miss partner lookup ───────────────────────────────────────────────

    private static CandidateCluster? FindNearMissPartner(
        CandidateCluster            cluster,
        IReadOnlyList<CandidateCluster> all)
    {
        return all.FirstOrDefault(c =>
            c.ClusterId != cluster.ClusterId &&
            c.Flags.Contains(ResolutionFlags.PossibleSameEntity) &&
            FuzzyMatcher.Similarity(c.ComparisonKey, cluster.ComparisonKey) >= CollisionStage.NearMissThreshold &&
            FuzzyMatcher.Similarity(c.ComparisonKey, cluster.ComparisonKey) <  ClusteringStage.ReviewThreshold);
    }

    // ── Signal helpers ─────────────────────────────────────────────────────────

    private static string? GetPrimarySignal(CandidateCluster c) =>
        c.Members
         .Select(m => m.Normalized.Candidate.Signals)
         .Where(s => s != null)
         .Select(s => s!.Domain ?? s.TaxId)
         .FirstOrDefault(v => v != null);

    private static bool SharesAnySignal(CandidateCluster a, CandidateCluster b) =>
        a.Members.Any(ma =>
            b.Members.Any(mb =>
                SignalMatcher.HasMatch(
                    ma.Normalized.Candidate.Signals,
                    mb.Normalized.Candidate.Signals)));

    private static string? GetSharedSignal(CandidateCluster a, CandidateCluster b)
    {
        foreach (var ma in a.Members)
        {
            var sa = ma.Normalized.Candidate.Signals;
            if (sa == null) continue;
            foreach (var mb in b.Members)
            {
                var sb = mb.Normalized.Candidate.Signals;
                if (sb == null) continue;
                if (sa.Domain != null && sb.Domain != null &&
                    sa.Domain.Equals(sb.Domain, StringComparison.OrdinalIgnoreCase))
                    return sa.Domain;
                if (sa.TaxId != null && sb.TaxId != null &&
                    sa.TaxId.Equals(sb.TaxId, StringComparison.OrdinalIgnoreCase))
                    return sa.TaxId;
            }
        }
        return null;
    }

    private static IReadOnlyList<string> AddFlag(IReadOnlyList<string> flags, string flag) =>
        flags.Contains(flag) ? flags : new List<string>(flags) { flag };
}
