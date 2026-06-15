using Ii.Contracts;
using Kozmo.Contracts;
using Kozmo.Contracts.Config;

namespace Ii.Posture;

/// <summary>
/// Assigns a deterministic stance from band + trend pattern + renewal proximity.
/// Pattern is derived by comparing current composite with the previous index composite.
/// </summary>
public sealed class PostureModule : IPostureModule
{
    private const double PatternThreshold = 0.03; // composite delta required to call Improving/Declining

    public PostureAssignment Assign(
        EntityIndex          index,
        EntityIndex?         previousIndex,
        DateTimeOffset?      contractRenewalDate,
        SaasProfile          profile,
        DateTimeOffset       now,
        MetaCognitionResult? meta = null)
    {
        var pattern      = DerivePattern(index, previousIndex);
        var renewalDays  = contractRenewalDate.HasValue
            ? (int)(contractRenewalDate.Value - now).TotalDays
            : (int?)null;

        var stance    = SelectStance(index.Band, pattern, renewalDays, profile);
        var rationale = BuildRationale(index, pattern, renewalDays);
        var evidence  = BuildEvidence(index);

        var contradictionCount = meta?.Contradictions.Count ?? 0;
        var confidence         = Math.Clamp(index.ConfidenceFloor - 0.10 * contradictionCount, 0.0, 0.95);

        IReadOnlyList<string> cautions     = meta?.Contradictions.Select(c => c.Description).ToList() ?? [];
        IReadOnlyList<string> evidenceGaps = meta?.Gaps.Select(g => g.Description).ToList()           ?? [];

        return new PostureAssignment(
            Id:            Guid.NewGuid(),
            EntityId:      index.EntityId,
            Band:          index.Band,
            Stance:        stance,
            Rationale:     rationale,
            EvidenceTrail: evidence,
            Confidence:    confidence,
            Fingerprint:   index.Fingerprint,
            IndexVersion:  index.Version,
            AssignedAt:    now,
            ValidUntil:    null)
        {
            Cautions     = cautions,
            EvidenceGaps = evidenceGaps
        };
    }

    private static TrendPattern DerivePattern(EntityIndex current, EntityIndex? previous)
    {
        if (previous == null) return TrendPattern.Stable;

        var delta = current.Composite - previous.Composite;
        if (delta > PatternThreshold)  return TrendPattern.Improving;
        if (delta < -PatternThreshold) return TrendPattern.Declining;
        return TrendPattern.Stable;
    }

    private static Stance SelectStance(Band band, TrendPattern pattern, int? renewalDays, SaasProfile profile)
    {
        var bandStr    = band.ToString();
        var patternStr = pattern.ToString();

        // Evaluate rules in order; first match wins.
        foreach (var rule in profile.PostureRules)
        {
            if (!rule.Band.Equals(bandStr, StringComparison.OrdinalIgnoreCase)) continue;

            var patternMatch = rule.Pattern.Equals("any", StringComparison.OrdinalIgnoreCase)
                            || rule.Pattern.Equals(patternStr, StringComparison.OrdinalIgnoreCase);
            if (!patternMatch) continue;

            var renewalMatch = rule.RenewalWithinDays == null
                            || (renewalDays.HasValue && renewalDays.Value <= rule.RenewalWithinDays.Value);
            if (!renewalMatch) continue;

            if (Enum.TryParse<Stance>(rule.Stance, ignoreCase: true, out var stance))
                return stance;
        }

        return band switch
        {
            Band.Healthy  => Stance.Maintain,
            Band.AtRisk   => Stance.Renegotiate,
            Band.Critical => Stance.Escalate,
            _             => Stance.Monitor
        };
    }

    private static string BuildRationale(EntityIndex index, TrendPattern pattern, int? renewalDays)
    {
        var parts = new List<string>
        {
            $"Band: {index.Band}",
            $"Composite: {index.Composite:F3}",
            $"Pattern: {pattern}",
            $"ConfidenceFloor: {index.ConfidenceFloor:F3}"
        };
        if (renewalDays.HasValue)
            parts.Add($"RenewalInDays: {renewalDays}");
        return string.Join("; ", parts);
    }

    private static IReadOnlyList<string> BuildEvidence(EntityIndex index)
    {
        var items = new List<string>
        {
            $"fingerprint:{index.Fingerprint}",
            $"index_v{index.Version}_at:{index.ComputedAt:O}"
        };
        foreach (var kv in index.DimensionScores.OrderBy(kv => kv.Key.ToString()))
            items.Add($"{kv.Key}:{kv.Value.Score:F3}(conf={kv.Value.Confidence:F3})");
        return items;
    }
}
