using Kozmo.Contracts;
using Kozmo.Contracts.Config;

namespace Kozmo.Platform.Analysis;

/// <summary>
/// Domain-agnostic contradiction detection over an arbitrary set of beliefs.
/// Two passes: supersession-delta (scored keys) and cross-source tier-disagreement (structural keys).
/// Called by IiFacade.ComputeMeta and reusable by Identity Resolution (§0, KYV Phase 1 spec).
/// </summary>
public static class ContradictionDetector
{
    /// <summary>
    /// |value_delta| threshold for a supersession to be flagged as a contradiction.
    /// Designed for the normalised [0,1] score range; do not apply to unbounded structural values.
    /// </summary>
    public const double SupersessionThreshold = 0.30;

    /// <summary>
    /// Supersession pass: for each belief that supersedes a predecessor in the same
    /// (Dimension, Criterion) slot, emit a Contradiction when |value delta| ≥ SupersessionThreshold
    /// and the claim_key is not structural (structural supersession is correct versioning, not conflict).
    /// </summary>
    public static IReadOnlyList<Contradiction> DetectSupersession(
        string entityId,
        IReadOnlyList<Belief> decayed,
        IReadOnlyList<Belief> allHistory,
        IReadOnlyDictionary<string, ClaimKeyDefinition> claimKeyCatalogue)
    {
        var contradictions = new List<Contradiction>();

        foreach (var b in decayed)
        {
            var predecessor = allHistory
                .FirstOrDefault(h => h.SupersededBy == b.Id
                                  && h.Dimension     == b.Dimension
                                  && h.Criterion     == b.Criterion);
            if (predecessor == null) continue;

            if (!string.IsNullOrEmpty(b.ClaimKey)
                && claimKeyCatalogue.TryGetValue(b.ClaimKey, out var ckDef)
                && ckDef.ClaimClass == "structural")
                continue;

            var delta = Math.Abs(b.Value - predecessor.Value);
            if (delta < SupersessionThreshold) continue;

            var severity = delta >= 0.70 ? ContradictionSeverity.High
                         : delta >= 0.50 ? ContradictionSeverity.Medium
                         :                 ContradictionSeverity.Low;

            contradictions.Add(new Contradiction(
                EntityId:             entityId,
                Dimension:            b.Dimension?.ToString() ?? "",
                Description:          $"{b.Dimension}/{b.Criterion}: new value {b.Value:F2} diverges from prior {predecessor.Value:F2} (Δ={delta:F2})",
                Severity:             severity,
                ConflictingBeliefIds: new List<Guid> { predecessor.Id, b.Id },
                DetectedBy:           DetectionSource.Deterministic));
        }

        return contradictions;
    }

    /// <summary>
    /// Cross-source pass: when two or more active beliefs share a structural claim_key but carry
    /// conflicting values from different source tiers, emit a Contradiction for each challenger
    /// (winner = highest-rank tier; challengers = everything below it with a non-zero value delta).
    /// Scored claim_keys are excluded — multi-source scored beliefs are fused by the Rubric, not contradictory.
    /// </summary>
    public static IReadOnlyList<Contradiction> DetectCrossSource(
        string entityId,
        IReadOnlyList<Belief> decayed,
        IReadOnlyDictionary<string, ClaimKeyDefinition> claimKeyCatalogue)
    {
        var contradictions = new List<Contradiction>();

        foreach (var grp in decayed
            .Where(b => !string.IsNullOrEmpty(b.ClaimKey))
            .GroupBy(b => b.ClaimKey))
        {
            if (grp.Count() < 2) continue;

            if (!claimKeyCatalogue.TryGetValue(grp.Key, out var ckDef) ||
                ckDef.ClaimClass != "structural")
                continue;

            var ordered = grp.OrderByDescending(b => TierRank(b.SourceTier)).ToList();
            var winner  = ordered[0];

            foreach (var challenger in ordered.Skip(1))
            {
                var delta = Math.Abs(winner.Value - challenger.Value);
                if (delta < 1e-9) continue;

                contradictions.Add(new Contradiction(
                    EntityId:             entityId,
                    Dimension:            winner.Dimension?.ToString() ?? "",
                    Description:          $"claim_key {grp.Key}: {winner.SourceTier} value {winner.Value:G} " +
                                          $"conflicts with {challenger.SourceTier} value {challenger.Value:G} " +
                                          $"(cross-source; {winner.SourceTier} is active)",
                    Severity:             ContradictionSeverity.Medium,
                    ConflictingBeliefIds: new List<Guid> { winner.Id, challenger.Id },
                    DetectedBy:           DetectionSource.Deterministic));
            }
        }

        return contradictions;
    }

    private static int TierRank(SourceTier t) => t switch
    {
        SourceTier.Primary    => 4,
        SourceTier.Verified   => 3,
        SourceTier.Reported   => 2,
        SourceTier.Inferred   => 1,
        _                     => 0,
    };
}
