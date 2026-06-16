using Ii.Contracts;
using Ii.Spine;
using Kozmo.Contracts;
using Kozmo.Contracts.Config;

namespace Kozmo.Api;

internal static class DtoMapper
{
    public static VendorSummaryDto ToSummary(
        Guid entityId, EntityRecord entity,
        EntityIndex? idx, PostureAssignment? posture,
        DateTimeOffset asOf) =>
        new(
            EntityId:    entityId.ToString(),
            Name:        entity.CanonicalName,
            Band:        idx?.Band.ToString()        ?? "Unknown",
            Stance:      posture?.Stance.ToString()  ?? "Unknown",
            Confidence:  posture?.Confidence         ?? 0.0,
            Fingerprint: idx?.Fingerprint            ?? string.Empty,
            AsOf:        asOf);

    public static VendorDetailDto ToDetail(
        Guid entityId, EntityRecord entity,
        EntityIndex idx, PostureAssignment posture,
        SaasProfile profile, DateTimeOffset asOf) =>
        new(
            EntityId: entityId.ToString(),
            Name:     entity.CanonicalName,
            AsOf:     asOf,
            Index:    ToIndexView(idx, profile),
            Posture:  ToPostureView(posture, entity, asOf));

    public static IndexViewDto ToIndexView(EntityIndex idx, SaasProfile profile)
    {
        var dimViews = idx.DimensionScores
            .OrderBy(kv => kv.Key.ToString(), StringComparer.Ordinal)
            .Select(kv =>
            {
                var dim    = kv.Key.ToString();
                var weight = profile.DimensionWeights.TryGetValue(dim, out var w) ? w : 0.0;
                return new DimensionScoreViewDto(
                    Dimension:    dim,
                    Score:        kv.Value.Score,
                    Confidence:   kv.Value.Confidence,
                    Weight:       weight,
                    Contribution: kv.Value.Score * weight,
                    BeliefCount:  kv.Value.ContributingBeliefIds.Count);
            })
            .ToList();

        var worst = dimViews.Count > 0 ? dimViews.MinBy(d => d.Score) : null;

        return new IndexViewDto(
            Composite:       idx.Composite,
            ConfidenceFloor: idx.ConfidenceFloor,
            Band:            idx.Band.ToString(),
            Fingerprint:     idx.Fingerprint,
            ConfigVersion:   profile.ConfigVersion,
            BandDrivenBy:    idx.BandDrivenBy,
            WorstDimension:  worst is null ? null : new DimensionMinDto(worst.Dimension, worst.Score),
            Dimensions:      dimViews);
    }

    public static PostureViewDto ToPostureView(
        PostureAssignment posture, EntityRecord entity, DateTimeOffset asOf)
    {
        RenewalViewDto? renewal = null;
        if (entity.RenewalDate.HasValue)
        {
            var days = (int)(entity.RenewalDate.Value - asOf).TotalDays;
            renewal = new RenewalViewDto(
                RenewalDate:   entity.RenewalDate.Value,
                WindowActive:  days is >= 0 and <= 90,
                DaysToRenewal: days);
        }

        return new PostureViewDto(
            Stance:       posture.Stance.ToString(),
            Confidence:   posture.Confidence,
            Rationale:    posture.Rationale,
            Cautions:     posture.Cautions,
            EvidenceGaps: posture.EvidenceGaps,
            Renewal:      renewal);
    }

    public static ReasoningTrailDto ToTrail(
        EntityIndex idx, PostureAssignment posture, EntityRecord entity,
        IReadOnlyList<Belief> beliefs, IReadOnlyList<Signal> signals,
        SaasProfile profile, DateTimeOffset asOf)
    {
        var signalMap  = signals.ToDictionary(s => s.Id);
        var byDim      = beliefs.GroupBy(b => b.Dimension).ToDictionary(g => g.Key, g => g.ToList());

        var bandView = new BandViewDto(
            Band: idx.Band.ToString(),
            Thresholds: new BandThresholdsDto(
                Critical: 0.0,
                AtRisk:   profile.Bands.AtRiskMin,
                Healthy:  profile.Bands.HealthyMin),
            DrivenBy: idx.BandDrivenBy);

        var dimViews = idx.DimensionScores
            .OrderBy(kv => kv.Key.ToString(), StringComparer.Ordinal)
            .Select(kv =>
            {
                var dim        = kv.Key.ToString();
                var weight     = profile.DimensionWeights.TryGetValue(dim, out var w) ? w : 0.0;
                var dimBeliefs = byDim.TryGetValue(kv.Key, out var bl) ? bl : [];

                var beliefViews = dimBeliefs
                    .OrderBy(b => b.CreatedAt)
                    .Select(b =>
                    {
                        Signal? sig = null;
                        foreach (var sid in b.SourceSignals)
                            if (signalMap.TryGetValue(sid, out var found)) { sig = found; break; }

                        return new BeliefViewDto(
                            BeliefId:             b.Id.ToString(),
                            Dimension:            b.Dimension.ToString(),
                            Criterion:            b.Criterion,
                            Value:                b.Value,
                            Confidence:           b.Confidence,
                            SourceTier:           b.SourceTier.ToString(),
                            ClassificationMethod: b.ClassificationMethod.ToString().ToLowerInvariant(),
                            ReasoningSummary:     b.ReasoningSummary,
                            Freshness:            b.Freshness,
                            Signal:               sig is null ? null : ToSignalRef(sig));
                    })
                    .ToList();

                return new DimensionBeliefViewDto(
                    Dimension:  dim,
                    Score:      kv.Value.Score,
                    Confidence: kv.Value.Confidence,
                    Weight:     weight,
                    Beliefs:    beliefViews);
            })
            .ToList();

        return new ReasoningTrailDto(
            Posture:    ToPostureView(posture, entity, asOf),
            Band:       bandView,
            Index:      ToIndexView(idx, profile),
            Dimensions: dimViews);
    }

    public static TrajectoryPointDto ToTrajectoryPoint(TrajectoryPoint pt) =>
        new(
            Timestamp:   pt.Timestamp,
            SignalId:    pt.SignalId?.ToString(),
            Composite:   pt.Composite,
            Band:        pt.Band.ToString(),
            Stance:      pt.Stance.ToString(),
            Fingerprint: pt.Fingerprint);

    private static SignalRefDto ToSignalRef(Signal s)
    {
        var summary = s.Payload.TryGetValue("body_excerpt", out var b) ? b?.ToString()
                    : s.Payload.TryGetValue("uptime_pct",   out var u) ? $"Uptime: {u}%"
                    : s.Payload.TryGetValue("adoption_pct", out var a) ? $"Adoption: {a}%"
                    : s.Payload.TryGetValue("csat_score",   out var c) ? $"CSAT: {c}/5"
                    : s.Payload.TryGetValue("days_overdue", out var d) ? $"{d} days overdue"
                    : s.Payload.TryGetValue("overdue_amount_usd", out var o) ? $"${o:N0} overdue"
                    : s.Payload.TryGetValue("roadmap_fit_score",  out var r) ? $"Roadmap fit: {r}"
                    : s.Payload.TryGetValue("renewal_intent",     out var n) ? $"Renewal intent: {n}"
                    : null;

        return new SignalRefDto(
            SignalId:  s.Id.ToString(),
            Type:      s.SourceSystem.ToString(),
            Timestamp: s.ObservedAt,
            Source:    s.ExternalId,
            Summary:   summary);
    }
}
