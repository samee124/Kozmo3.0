using Ii.Contracts;
using Kozmo.Contracts;
using Kozmo.Contracts.Config;
using Kozmo.Contracts.Interfaces;

namespace Ii.Spine;

/// <summary>
/// Pipeline orchestrator. The only component that reads the clock.
/// Wires: Signal → Observation → Belief → Decay → Rubric → Index → Posture.
/// </summary>
public sealed class IiFacade : IIiFacade
{
    private readonly IObservationModule _observation;
    private readonly IRubricModule      _rubric;
    private readonly IIndexModule       _index;
    private readonly IPostureModule     _posture;
    private readonly IDecayEngine       _decay;
    private readonly IEntityStore       _store;
    private readonly SaasProfile        _profile;
    private readonly EntityRegistry     _registry;
    private readonly IClock             _clock;

    public IiFacade(
        IObservationModule observation,
        IRubricModule      rubric,
        IIndexModule       index,
        IPostureModule     posture,
        IDecayEngine       decay,
        IEntityStore       store,
        SaasProfile        profile,
        EntityRegistry     registry,
        IClock?            clock = null)
    {
        _observation = observation;
        _rubric      = rubric;
        _index       = index;
        _posture     = posture;
        _decay       = decay;
        _store       = store;
        _profile     = profile;
        _registry    = registry;
        _clock       = clock ?? new WallClock();
    }

    public async Task<Guid> SubmitSignalAsync(Signal signal, CancellationToken ct = default)
    {
        var now = _clock.UtcNow;  // clock read — only here in Spine

        await _store.AppendSignalAsync(signal, ct);

        var classification = _observation.Classify(signal, _profile);
        if (classification == null) return signal.TraceId;

        // Resolve entity (alias map applies here)
        var entityId = _registry.Resolve(signal.EntityId, signal.Payload, _profile);

        var tierWeight = _profile.SourceTiers.TryGetValue(classification.SourceTier.ToString(), out var tc)
            ? tc.Weight : 0.0;
        var freshness  = _decay.ComputeFreshness(
            new Belief(Guid.Empty, entityId, classification.Dimension, classification.Criterion,
                       classification.Value, classification.SourceTier, 1.0, 1.0, "", [], 1, null,
                       signal.ObservedAt, signal.TraceId),
            _profile, now);
        var confidence = tierWeight * freshness;

        // Supersede prior belief for same (entity, dimension, criterion) if any
        var priorBeliefs = await _store.GetCurrentBeliefsAsync(entityId, ct);
        var priorForSlot = priorBeliefs
            .FirstOrDefault(b => b.Dimension == classification.Dimension
                              && b.Criterion  == classification.Criterion);

        var version = priorForSlot?.Version + 1 ?? 1;

        var belief = new Belief(
            Id:            Guid.NewGuid(),
            EntityId:      entityId,
            Dimension:     classification.Dimension,
            Criterion:     classification.Criterion,
            Value:         classification.Value,
            SourceTier:    classification.SourceTier,
            Confidence:    confidence,
            Freshness:     freshness,
            Derivation:    classification.Derivation,
            SourceSignals: [signal.Id],
            Version:       version,
            SupersededBy:  null,
            CreatedAt:     now,
            TraceId:       signal.TraceId)
        {
            ClassificationMethod     = classification.Method,
            ClassificationConfidence = classification.MethodConfidence,
            ReasoningSummary         = classification.ReasoningSummary
        };

        await _store.AppendBeliefAsync(belief, ct);

        // Mark prior belief as superseded (append a corrected version pointing back)
        if (priorForSlot != null)
        {
            var superseded = priorForSlot with { SupersededBy = belief.Id };
            await _store.AppendBeliefAsync(superseded, ct);
        }

        // Recompute index
        await RecomputeIndexAsync(entityId, classification.Dimension, now, ct);

        return signal.TraceId;
    }

    public Task<PostureAssignment?> GetPostureAsync(Guid entityId, CancellationToken ct = default) =>
        _store.GetCurrentPostureAsync(entityId, ct);

    public Task<EntityIndex?> GetIndexAsync(Guid entityId, CancellationToken ct = default) =>
        _store.GetIndexAsync(entityId, ct);

    public Task<IReadOnlyList<Belief>> GetBeliefsAsync(Guid entityId, CancellationToken ct = default) =>
        _store.GetCurrentBeliefsAsync(entityId, ct);

    public async Task<ReasoningTrail?> GetReasoningTrailAsync(Guid entityId, CancellationToken ct = default)
    {
        var posture    = await _store.GetCurrentPostureAsync(entityId, ct);
        var idx        = await _store.GetIndexAsync(entityId, ct);
        var beliefs    = await _store.GetCurrentBeliefsAsync(entityId, ct);
        var allHistory = await _store.GetBeliefHistoryAsync(entityId, ct);

        var signals = new List<Signal>();
        foreach (var b in beliefs)
        foreach (var sid in b.SourceSignals)
        {
            var sig = await _store.GetSignalAsync(sid, ct);
            if (sig != null) signals.Add(sig);
        }

        MetaCognitionResult? meta      = null;
        IReadOnlyList<Belief> trailBeliefs = beliefs; // default: stored beliefs (no data yet)
        if (beliefs.Count > 0)
        {
            var now      = _clock.UtcNow;
            var decayed  = beliefs.Select(b => _decay.WithCurrentFreshness(b, _profile, now)).ToList();
            var anchored = AnchorConfidences(decayed, allHistory, now);
            meta         = ComputeMeta(entityId, decayed, anchored, allHistory, now);
            // Return anchored beliefs so the drill-down exposes current freshness + anchor provenance.
            trailBeliefs = anchored;
        }

        return new ReasoningTrail(entityId, posture, idx, trailBeliefs, signals) { Meta = meta };
    }

    public async Task<IReadOnlyList<TrajectoryPoint>> GetTrajectoryAsync(
        Guid entityId, CancellationToken ct = default)
    {
        var indices  = await _store.GetIndexHistoryAsync(entityId, ct);
        var postures = await _store.GetPostureHistoryAsync(entityId, ct);
        var signals  = await _store.GetSignalsForEntityAsync(entityId, ct);

        var postureByVersion = postures.ToDictionary(p => p.IndexVersion);

        return indices
            .OrderBy(idx => idx.Version)
            .Select((idx, i) =>
            {
                var posture = postureByVersion.TryGetValue(idx.Version, out var p) ? p : null;
                Guid? sigId = i < signals.Count ? signals[i].Id : null;
                return new TrajectoryPoint(
                    Timestamp:   idx.ComputedAt,
                    SignalId:    sigId,
                    Composite:   idx.Composite,
                    Band:        idx.Band,
                    Stance:      posture?.Stance ?? Stance.Monitor,
                    Fingerprint: idx.Fingerprint);
            })
            .ToList();
    }

    public Task ResetAsync(CancellationToken ct = default) => _store.ResetAsync(ct);

    // ── Private ───────────────────────────────────────────────────────────────

    private async Task RecomputeIndexAsync(Guid entityId, Dimension dirtyDim, DateTimeOffset now, CancellationToken ct)
    {
        var allBeliefs = await _store.GetCurrentBeliefsAsync(entityId, ct);
        var allHistory = await _store.GetBeliefHistoryAsync(entityId, ct);

        // Apply decay to all current beliefs
        var decayed = allBeliefs
            .Select(b => _decay.WithCurrentFreshness(b, _profile, now))
            .ToList();

        // Confidence anchor: corroborating evidence must never lower a dimension's effective
        // confidence below the strongest still-valid predecessor in the same slot.
        // For each current belief, if it superseded a predecessor with higher decayed confidence,
        // floor the current belief's confidence at the predecessor's decayed confidence.
        // This ensures that adding Reported evidence that confirms a Verified bad signal
        // cannot demote the band by collapsing the confidence floor below 0.60.
        var anchored = AnchorConfidences(decayed, allHistory, now);

        var previous = await _store.GetIndexAsync(entityId, ct);

        EntityIndex newIndex;
        if (previous == null)
        {
            // First signal — full aggregate
            var scores = BuildAllDimensionScores(entityId, anchored);
            newIndex = _index.Aggregate(entityId, scores, anchored, null, _profile, now);
        }
        else
        {
            // Incremental — only recompute the dirty dimension
            var dimBeliefs = anchored.Where(b => b.Dimension == dirtyDim).ToList();
            var dimScore   = _rubric.ScoreDimension(entityId, dirtyDim, dimBeliefs, _profile);
            newIndex = _index.RecomputeDirty(entityId, dirtyDim, dimScore, anchored, previous, _profile, now);
        }

        await _store.SaveIndexAsync(newIndex, ct);

        var meta    = ComputeMeta(entityId, decayed, anchored, allHistory, now);
        var entity  = _registry.GetEntity(entityId);
        var posture = _posture.Assign(newIndex, previous, entity?.RenewalDate, _profile, now, meta);
        await _store.AppendPostureAsync(posture, ct);
    }

    /// <summary>
    /// For each current belief, check whether its direct predecessor (the belief it superseded)
    /// had a higher decayed confidence. If so, raise the current belief's effective confidence
    /// to that predecessor's level. When the anchor fires, sets AnchorRawConfidence,
    /// AnchorPredecessorId, and AnchorPredecessorTier annotation fields for provenance.
    /// The stored belief is unchanged; this affects scoring and the reasoning trail only.
    /// </summary>
    private IReadOnlyList<Belief> AnchorConfidences(
        IReadOnlyList<Belief> decayed,
        IReadOnlyList<Belief> allHistory,
        DateTimeOffset now)
    {
        var result = new List<Belief>(decayed.Count);
        foreach (var b in decayed)
        {
            // Find the best predecessor by decayed confidence in the same (Dimension, Criterion) slot.
            var predecessors = allHistory
                .Where(h => h.SupersededBy == b.Id
                         && h.Dimension   == b.Dimension
                         && h.Criterion   == b.Criterion)
                .Select(h => (belief: h, conf: _decay.WithCurrentFreshness(h, _profile, now).Confidence))
                .ToList();

            if (predecessors.Count == 0)
            {
                result.Add(b);
                continue;
            }

            var best = predecessors.MaxBy(x => x.conf);
            if (b.Confidence < best.conf)
            {
                result.Add(b with
                {
                    Confidence            = best.conf,
                    AnchorRawConfidence   = b.Confidence,
                    AnchorPredecessorId   = best.belief.Id,
                    AnchorPredecessorTier = best.belief.SourceTier
                });
            }
            else
            {
                result.Add(b);
            }
        }
        return result;
    }

    /// <summary>
    /// Compute a deterministic MetaCognitionResult from the current (decayed + anchored) belief set.
    /// Contradictions: current value diverges ≥ 0.30 from direct predecessor in the same slot.
    /// Gaps: a dimension has no current belief at all.
    /// EpistemicSummary: plain-language description of any confidence anchors that fired.
    /// </summary>
    private MetaCognitionResult ComputeMeta(
        Guid entityId,
        IReadOnlyList<Belief> decayed,
        IReadOnlyList<Belief> anchored,
        IReadOnlyList<Belief> allHistory,
        DateTimeOffset now)
    {
        const double ContradictionThreshold = 0.30;

        var contradictions = new List<Contradiction>();
        var anchorNotes    = new List<string>();

        // Contradiction detection: superseding belief value diverges from its predecessor
        foreach (var b in decayed)
        {
            var predecessor = allHistory
                .FirstOrDefault(h => h.SupersededBy == b.Id
                                  && h.Dimension     == b.Dimension
                                  && h.Criterion     == b.Criterion);
            if (predecessor == null) continue;

            var delta = Math.Abs(b.Value - predecessor.Value);
            if (delta < ContradictionThreshold) continue;

            var severity = delta >= 0.70 ? ContradictionSeverity.High
                         : delta >= 0.50 ? ContradictionSeverity.Medium
                         :                 ContradictionSeverity.Low;

            contradictions.Add(new Contradiction(
                EntityId:             entityId.ToString(),
                Dimension:            b.Dimension.ToString(),
                Description:          $"{b.Dimension}/{b.Criterion}: new value {b.Value:F2} diverges from prior {predecessor.Value:F2} (\u0394={delta:F2})",
                Severity:             severity,
                ConflictingBeliefIds: new List<Guid> { predecessor.Id, b.Id },
                DetectedBy:           DetectionSource.Deterministic));
        }

        // Gap detection: dimensions with no current evidence
        var gaps        = new List<Gap>();
        var coveredDims = decayed.Select(b => b.Dimension).ToHashSet();
        foreach (var dim in Enum.GetValues<Dimension>())
        {
            if (!coveredDims.Contains(dim))
                gaps.Add(new Gap(
                    EntityId:    entityId.ToString(),
                    Dimension:   dim.ToString(),
                    Description: $"{dim}: no current evidence available.",
                    DetectedBy:  DetectionSource.Deterministic));
        }

        // Anchor trace: beliefs where confidence was raised by AnchorConfidences
        foreach (var (orig, anch) in decayed.Zip(anchored))
        {
            if (anch.Confidence > orig.Confidence + 1e-6)
                anchorNotes.Add(
                    $"{anch.Dimension}/{anch.Criterion}: confidence anchored " +
                    $"{orig.Confidence:F3}\u2192{anch.Confidence:F3} " +
                    $"(floor from prior {orig.SourceTier} belief)");
        }

        var summary = anchorNotes.Count > 0
            ? string.Join("; ", anchorNotes)
            : "No confidence anchors active.";

        return new MetaCognitionResult(
            EntityId:         entityId.ToString(),
            Contradictions:   contradictions,
            Gaps:             gaps,
            EpistemicSummary: summary);
    }

    private IReadOnlyDictionary<Dimension, DimensionScore> BuildAllDimensionScores(
        Guid entityId, IReadOnlyList<Belief> decayed)
    {
        var byDim = decayed.GroupBy(b => b.Dimension);
        return byDim.ToDictionary(
            g => g.Key,
            g => _rubric.ScoreDimension(entityId, g.Key, g.ToList(), _profile));
    }
}
