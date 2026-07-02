using System.Text.RegularExpressions;
using Ig.Contracts;

// §0 REUSE NOTE: ContradictionDetector (Kozmo.Platform.Analysis) is the platform's
// cross-claim conflict surface and operates on typed Belief objects with
// Dimension / Criterion / Value schema. CandidateIdentityBelief signal comparison
// (domain / tax_id) is structural identity matching, not scored belief reconciliation —
// ContradictionDetector is not applicable here.
// The SourceTier numeric ordering used for canonical-name preference is inherent in the
// enum's integer values (Primary=4 > Verified=3 > Reported=2 > Inferred=1), not
// reimplemented. ConfidenceClamper applies upstream: CandidateIdentityBelief.Confidence
// is guaranteed ≤ tier ceiling before entering this stage (spec §1.1).

namespace Ig.Resolution;

/// <summary>
/// Stage C — Cluster + canonicalize.
/// Groups non-dropped ClassifiedCandidates into CandidateClusters by comparison_key
/// (exact match or fuzzy similarity above MERGE_THRESHOLD). Conflicting signals block
/// a merge regardless of key similarity; those pairs surface as separate clusters and
/// Stage D (CollisionStage) adds the COLLISION / SUSPECTED_REBRAND flags.
/// </summary>
public sealed class ClusteringStage
{
    /// <summary>
    /// Conservative merge threshold (normalized Levenshtein ≥ this → auto-merge).
    /// Set high intentionally: a wrong merge is a HARD fail; a split duplicate is fixable.
    /// </summary>
    public const double MergeThreshold = 0.90;

    /// <summary>
    /// Review threshold. Keys in [ReviewThreshold, MergeThreshold) → merged but flagged
    /// LOW_CONFIDENCE_MATCH + FUZZY_MATCH for Stage E disposition.
    /// </summary>
    public const double ReviewThreshold = 0.75;

    // Regex: detect legal suffix words in a raw_name to prefer legally-complete names.
    private static readonly Regex _legalSuffixInName = new(
        @"\b(inc\.?|llc|ltd\.?|limited|gmbh|corp\.?|plc|sa|bv|ag)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // ── Public API ─────────────────────────────────────────────────────────────

    public IReadOnlyList<CandidateCluster> Cluster(
        IReadOnlyList<ClassifiedCandidate> classified)
    {
        // Only COMPANY / UNKNOWN proceed; PERSON / INTERNAL / etc. are excluded.
        var candidates = classified.Where(c => !c.IsDropped).ToList();

        var open = new List<MutableCluster>();

        foreach (var candidate in candidates)
        {
            bool merged = false;

            foreach (var cluster in open)
            {
                double sim = FuzzyMatcher.Similarity(
                    candidate.Normalized.ComparisonKey,
                    cluster.ComparisonKey);

                bool aboveMerge  = sim >= MergeThreshold;
                bool aboveReview = sim >= ReviewThreshold;

                // Signal conflict blocks merge at any similarity level.
                // Conflicting pairs form separate clusters; Stage D adds COLLISION.
                bool conflict = cluster.Members.Any(m =>
                    SignalMatcher.HasConflict(
                        candidate.Normalized.Candidate.Signals,
                        m.Normalized.Candidate.Signals));

                if (aboveMerge && !conflict)
                {
                    cluster.Members.Add(candidate);
                    if (sim < 1.0) cluster.Flags.Add(ResolutionFlags.FuzzyMatch);
                    merged = true;
                    break;
                }

                if (aboveReview && !aboveMerge && !conflict)
                {
                    cluster.Members.Add(candidate);
                    cluster.Flags.Add(ResolutionFlags.LowConfidenceMatch);
                    cluster.Flags.Add(ResolutionFlags.FuzzyMatch);
                    merged = true;
                    break;
                }
            }

            if (!merged)
                open.Add(new MutableCluster(candidate));
        }

        return open.Select(Build).ToList();
    }

    // ── Private helpers ────────────────────────────────────────────────────────

    private static CandidateCluster Build(MutableCluster mc)
    {
        var members   = mc.Members.AsReadOnly();
        var canonical = SelectCanonicalMember(members);

        EntityType type = members.Any(m => m.EntityType == EntityType.Company)
            ? EntityType.Company : EntityType.Unknown;

        // Confidence = max across members (pre-clamped upstream per §1.1).
        double confidence = members.Max(m => m.Normalized.Candidate.Confidence);

        return new CandidateCluster(
            ClusterId:     Guid.NewGuid(),
            Members:       members,
            CanonicalName: canonical.Normalized.EffectiveName,
            ComparisonKey: canonical.Normalized.ComparisonKey,
            EntityType:    type,
            Confidence:    confidence,
            Flags:         mc.Flags.Distinct().ToList(),
            EntityRole:    AggregateEntityRole(members));
    }

    /// <summary>
    /// Determines the cluster's entity role from all member beliefs.
    /// Highest-tier-wins: the member(s) with the highest SourceTier value vote on the role.
    /// Tie-break by majority; on a tie prefer customer > vendor > issuer > unknown (conservative:
    /// bias toward non-vendor on ambiguity). "unknown" is never treated as non-vendor downstream.
    /// </summary>
    private static string AggregateEntityRole(IReadOnlyList<ClassifiedCandidate> members)
    {
        if (members.Count == 0) return "unknown";

        var roled = members
            .Select(m => ((int)m.Normalized.Candidate.SourceTier,
                          (m.Normalized.Candidate.RoleHint ?? "unknown").ToLowerInvariant()))
            .ToList();

        var topTier = roled.Max(r => r.Item1);
        var topRoles = roled.Where(r => r.Item1 == topTier).Select(r => r.Item2).ToList();

        var votes    = topRoles.GroupBy(r => r).ToDictionary(g => g.Key, g => g.Count());
        var maxVotes = votes.Values.Max();
        var winners  = votes.Where(kv => kv.Value == maxVotes).Select(kv => kv.Key).ToList();

        if (winners.Count == 1) return winners[0];

        // Conservative tie-break: flag as non-vendor on any ambiguity
        foreach (var preferred in new[] { "customer", "vendor", "issuer", "unknown" })
            if (winners.Contains(preferred)) return preferred;

        return "unknown";
    }

    /// <summary>
    /// Prefer the member whose raw_name contains a legal entity suffix (most complete
    /// legal form). Among suffix-bearing members, prefer the most words then highest
    /// source tier. Fall back to longest name by word count.
    /// </summary>
    private static ClassifiedCandidate SelectCanonicalMember(
        IReadOnlyList<ClassifiedCandidate> members)
    {
        // Use EffectiveName (doc-title prefix already stripped) so "Amendment 3 – Aramark"
        // is evaluated as "Aramark", not the full document reference.
        var withSuffix = members
            .Where(m => _legalSuffixInName.IsMatch(m.Normalized.EffectiveName))
            .OrderByDescending(m => WordCount(m.Normalized.EffectiveName))
            .ThenByDescending(m => (int)m.Normalized.Candidate.SourceTier)
            .FirstOrDefault();

        if (withSuffix != null) return withSuffix;

        return members
            .OrderByDescending(m => WordCount(m.Normalized.EffectiveName))
            .ThenByDescending(m => (int)m.Normalized.Candidate.SourceTier)
            .First();
    }

    private static int WordCount(string s) =>
        s.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;

    // ── Mutable builder ────────────────────────────────────────────────────────

    private sealed class MutableCluster
    {
        public string ComparisonKey { get; }
        public List<ClassifiedCandidate> Members { get; } = new();
        public List<string>              Flags   { get; } = new();

        public MutableCluster(ClassifiedCandidate seed)
        {
            Members.Add(seed);
            ComparisonKey = seed.Normalized.ComparisonKey;
        }
    }
}
