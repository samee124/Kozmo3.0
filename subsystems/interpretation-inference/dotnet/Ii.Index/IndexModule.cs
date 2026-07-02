using Ii.Contracts;
using Kozmo.Contracts;
using Kozmo.Contracts.Config;
using Kozmo.Platform.Fingerprint;

namespace Ii.Index;

/// <summary>
/// Aggregates dimension scores into an EntityIndex.
/// Applies the confidence-floor CRITICAL gate (invariant #4):
///   composite below critical_threshold + confidence_floor &lt; 0.60 → elevate to AtRisk.
/// </summary>
public sealed class IndexModule : IIndexModule
{
    private static readonly Dimension[] AllDimensions =
        [Dimension.Operational, Dimension.Experiential, Dimension.Financial, Dimension.Strategic];

    public EntityIndex Aggregate(
        Guid                                          entityId,
        IReadOnlyDictionary<Dimension, DimensionScore> dimensionScores,
        IReadOnlyList<Belief>                         allBeliefs,
        EntityIndex?                                  previous,
        SaasProfile                                   profile,
        DateTimeOffset                                now)
    {
        var full = EnsureAllDimensions(entityId, dimensionScores);
        return Build(entityId, full, allBeliefs, previous, profile, now);
    }

    public EntityIndex RecomputeDirty(
        Guid                                          entityId,
        Dimension                                     dirtyDimension,
        DimensionScore                                newDimensionScore,
        IReadOnlyList<Belief>                         allBeliefs,
        EntityIndex                                   previous,
        SaasProfile                                   profile,
        DateTimeOffset                                now)
    {
        var merged = AllDimensions.ToDictionary(
            d => d,
            d => d == dirtyDimension
                ? newDimensionScore
                : previous.DimensionScores.TryGetValue(d, out var s) ? s
                  : NeutralScore(entityId, d));

        return Build(entityId, merged, allBeliefs, previous, profile, now);
    }

    public string ComputeFingerprint(
        IReadOnlyDictionary<Dimension, DimensionScore> dimensionScores,
        IReadOnlyList<Belief>                          allBeliefs,
        SaasProfile                                    profile)
    {
        var input = BuildFingerprintInput(dimensionScores, allBeliefs, profile);
        return FingerprintComputer.Compute(input);
    }

    // ── private helpers ──────────────────────────────────────────────────────

    private EntityIndex Build(
        Guid                                          entityId,
        IReadOnlyDictionary<Dimension, DimensionScore> scores,
        IReadOnlyList<Belief>                         allBeliefs,
        EntityIndex?                                  previous,
        SaasProfile                                   profile,
        DateTimeOffset                                now)
    {
        var composite       = ComputeComposite(scores, profile);
        var confidenceFloor = ComputeConfidenceFloor(scores);

        var compositeBand   = AssignBand(composite, confidenceFloor, profile);

        var withBeliefs     = scores.Values.Where(s => s.ContributingBeliefIds.Count > 0).ToList();
        var worstDimScore   = withBeliefs.Count > 0 ? withBeliefs.Min(s => s.Score) : composite;
        var worstBand       = AssignBand(worstDimScore, confidenceFloor, profile);

        var band            = MoreSevere(compositeBand, worstBand);
        var bandDrivenBy    = worstBand > compositeBand ? "worst-dimension-floor" : "composite";
        var version         = (previous?.Version ?? 0) + 1;
        var fingerprint     = ComputeFingerprint(scores, allBeliefs, profile);

        return new EntityIndex(entityId, scores, composite, confidenceFloor, band, fingerprint, version, now)
        {
            BandDrivenBy = bandDrivenBy
        };
    }

    private static Band MoreSevere(Band a, Band b) => a > b ? a : b;

    private static double ComputeComposite(
        IReadOnlyDictionary<Dimension, DimensionScore> scores,
        SaasProfile                                    profile)
    {
        double weightedSum = 0, totalWeight = 0;
        foreach (var dim in AllDimensions)
        {
            var w = profile.DimensionWeights.TryGetValue(dim.ToString(), out var wv) ? wv : 0.25;
            var s = scores.TryGetValue(dim, out var ds) ? ds.Score : 0.5;
            weightedSum += s * w;
            totalWeight += w;
        }
        return totalWeight > 0 ? weightedSum / totalWeight : 0;
    }

    private static double ComputeConfidenceFloor(
        IReadOnlyDictionary<Dimension, DimensionScore> scores)
    {
        var withBeliefs = scores.Values.Where(s => s.ContributingBeliefIds.Count > 0).ToList();
        return withBeliefs.Count > 0 ? withBeliefs.Min(s => s.Confidence) : 0.0;
    }

    private static Band AssignBand(double composite, double confidenceFloor, SaasProfile profile)
    {
        if (composite >= profile.Bands.HealthyMin) return Band.Healthy;
        if (composite >= profile.Bands.AtRiskMin)  return Band.AtRisk;
        return confidenceFloor >= profile.Bands.CriticalConfidenceGate ? Band.Critical : Band.AtRisk;
    }

    private static IReadOnlyDictionary<Dimension, DimensionScore> EnsureAllDimensions(
        Guid entityId, IReadOnlyDictionary<Dimension, DimensionScore> supplied)
    {
        var result = new Dictionary<Dimension, DimensionScore>();
        foreach (var dim in AllDimensions)
            result[dim] = supplied.TryGetValue(dim, out var s) ? s : NeutralScore(entityId, dim);
        return result;
    }

    private static DimensionScore NeutralScore(Guid entityId, Dimension dim) =>
        new(entityId, dim, 0.5, 0.0, []);

    private static FingerprintInput BuildFingerprintInput(
        IReadOnlyDictionary<Dimension, DimensionScore> scores,
        IReadOnlyList<Belief>                          allBeliefs,
        SaasProfile                                    profile)
    {
        var snapshots = allBeliefs
            .Select(b => new BeliefSnapshot(b.Dimension?.ToString() ?? "Financial", b.Criterion, b.Value, b.Confidence))
            .ToList();

        return new FingerprintInput(
            snapshots,
            scores.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value.Score),
            profile.DimensionWeights.ToDictionary(kv => kv.Key, kv => kv.Value),
            profile.ConfigVersion);
    }
}
