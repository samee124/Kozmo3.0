using Ig.Contracts;

namespace Ig.Resolution;

/// <summary>
/// Stage D — Collision + lifecycle flagging.
/// Pure annotation pass: does NOT mutate cluster membership, only adds flags.
/// Two rules (§2 Stage D):
///   COLLISION       — clusters with similar comparison_keys (≥ ReviewThreshold)
///                     AND conflicting signals. They were kept separate by Stage C;
///                     Stage D makes the reason visible.
///   SUSPECTED_REBRAND — clusters with different comparison_keys (&lt; ReviewThreshold)
///                     BUT matching signals (same domain or tax_id). Rebrand/acquisition
///                     maps are empty this phase, so all such pairs go to TRIAGE.
/// No Stage E (gate) or Stage F (Registry write) — those are Commit 3.
/// </summary>
public sealed class CollisionStage
{
    // Reuse Stage C's review threshold: "similar enough to warrant collision scrutiny".
    private const double SimilarNameThreshold = ClusteringStage.ReviewThreshold;

    /// <summary>
    /// Minimum similarity for a near-miss pair: name is similar but not similar enough to
    /// cluster. Pairs in [NearMissThreshold, ReviewThreshold) get POSSIBLE_SAME_ENTITY flag
    /// → Stage E sends them to TRIAGE for human confirmation. Pairs below this are unrelated.
    /// </summary>
    public const double NearMissThreshold = 0.40;

    public IReadOnlyList<CandidateCluster> Annotate(IReadOnlyList<CandidateCluster> clusters)
    {
        var result = clusters.ToArray();

        for (int i = 0; i < result.Length; i++)
        {
            for (int j = i + 1; j < result.Length; j++)
            {
                double sim     = FuzzyMatcher.Similarity(
                    result[i].ComparisonKey, result[j].ComparisonKey);
                bool conflict  = AnyConflict(result[i], result[j]);
                bool matching  = AnyMatch(result[i], result[j]);

                if (sim >= SimilarNameThreshold && conflict)
                {
                    result[i] = AddFlag(result[i], ResolutionFlags.Collision);
                    result[j] = AddFlag(result[j], ResolutionFlags.Collision);
                }
                else if (sim < SimilarNameThreshold && matching)
                {
                    result[i] = AddFlag(result[i], ResolutionFlags.SuspectedRebrand);
                    result[j] = AddFlag(result[j], ResolutionFlags.SuspectedRebrand);
                }
                else if (sim >= NearMissThreshold)
                {
                    // Name near-miss: similar but distinct (SUSPECTED_REBRAND didn't apply).
                    // Fires regardless of signal state — conflicting domains (e.g. abctech.com vs
                    // abctechnologies.com) increase uncertainty rather than exclude the pair.
                    // Stage E triages with a formed question naming both candidates.
                    result[i] = AddFlag(result[i], ResolutionFlags.PossibleSameEntity);
                    result[j] = AddFlag(result[j], ResolutionFlags.PossibleSameEntity);
                }
            }
        }

        return result;
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private static CandidateCluster AddFlag(CandidateCluster cluster, string flag)
    {
        if (cluster.Flags.Contains(flag)) return cluster;
        return cluster with { Flags = new List<string>(cluster.Flags) { flag } };
    }

    private static bool AnyConflict(CandidateCluster a, CandidateCluster b)
    {
        foreach (var ma in a.Members)
        foreach (var mb in b.Members)
            if (SignalMatcher.HasConflict(
                    ma.Normalized.Candidate.Signals,
                    mb.Normalized.Candidate.Signals))
                return true;
        return false;
    }

    private static bool AnyMatch(CandidateCluster a, CandidateCluster b)
    {
        foreach (var ma in a.Members)
        foreach (var mb in b.Members)
            if (SignalMatcher.HasMatch(
                    ma.Normalized.Candidate.Signals,
                    mb.Normalized.Candidate.Signals))
                return true;
        return false;
    }
}
