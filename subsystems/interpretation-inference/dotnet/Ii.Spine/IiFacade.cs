using Ii.Completeness;
using Ii.Contracts;
using Km.Store;
using Kozmo.Contracts;
using Kozmo.Contracts.Config;
using Kozmo.Contracts.Interfaces;
using Kozmo.Platform.Analysis;

namespace Ii.Spine;

/// <summary>
/// Pipeline orchestrator. The only component that reads the clock.
/// Wires: Signal → Observation → Belief → Decay → Rubric → Index → Posture.
/// </summary>
public sealed class IiFacade : IIiFacade
{
    private readonly IObservationModule      _observation;
    private readonly IRubricModule           _rubric;
    private readonly IIndexModule            _index;
    private readonly IPostureModule          _posture;
    private readonly IDecayEngine            _decay;
    private readonly IEntityStore            _store;
    private readonly SaasProfile             _profile;
    private readonly EntityRegistry          _registry;
    private readonly IClock                  _clock;
    private readonly CompletenessOrchestrator? _completeness;

    public IiFacade(
        IObservationModule         observation,
        IRubricModule              rubric,
        IIndexModule               index,
        IPostureModule             posture,
        IDecayEngine               decay,
        IEntityStore               store,
        SaasProfile                profile,
        EntityRegistry             registry,
        IClock?                    clock        = null,
        CompletenessOrchestrator?  completeness = null)
    {
        _observation  = observation;
        _rubric       = rubric;
        _index        = index;
        _posture      = posture;
        _decay        = decay;
        _store        = store;
        _profile      = profile;
        _registry     = registry;
        _clock        = clock ?? new WallClock();
        _completeness = completeness;
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

    public async Task<VendorJudgement> RecomputeVendorAsync(Guid entityId, CancellationToken ct = default)
    {
        var now        = _clock.UtcNow;
        var allBeliefs = await _store.GetCurrentBeliefsAsync(entityId, ct);
        var allHistory = await _store.GetBeliefHistoryAsync(entityId, ct);

        var decayed  = allBeliefs.Select(b => _decay.WithCurrentFreshness(b, _profile, now)).ToList();
        var anchored = AnchorConfidences(decayed, allHistory, now);

        var scores   = BuildAllDimensionScores(entityId, anchored);
        var index    = _index.Aggregate(entityId, scores, anchored, null, _profile, now);

        var entity   = _registry.GetEntity(entityId);
        var meta     = ComputeMeta(entityId, decayed, anchored, allHistory, now);
        var posture  = _posture.Assign(index, null, entity?.RenewalDate, _profile, now, meta);
        var mgmt     = ComputeManagementBlock(entityId, decayed, allBeliefs, scores, meta);

        // Phase 5: Q&A completeness convergence — synchronous, in the same recompute pass.
        // Production's completeness LLM is always replay-only (no live network in the demo
        // runtime path), so a belief combination that was never pre-recorded hits a cache miss.
        // CompletenessOrchestrator/QuestionAnsweringStage do not catch that internally, so without
        // a guard here it would propagate out of RecomputeVendorAsync and take down every caller —
        // the vendor-file page, the check-in answer flow, all of them (see IIiFacade.cs callers).
        // Mirrors KyvProgramRunner stage 8's per-vendor containment: a completeness failure here
        // degrades to "no completeness update this cycle," not a failed recompute. The index,
        // posture, meta, and management block above are already computed and remain valid.
        if (_completeness != null)
        {
            try
            {
                await _completeness.RunAsync(entityId, allBeliefs, now, ct);
            }
            catch (Exception)
            {
                // This recompute's completeness convergence is skipped — the caller still gets a
                // fully-formed VendorJudgement from the index/posture/meta/mgmt already computed.
            }
        }

        return new VendorJudgement(index, posture, meta, mgmt);
    }

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
    /// For each current belief, find the strongest still-valid ancestor in the same
    /// (Dimension, Criterion) slot by walking the FULL supersession chain (not just the
    /// direct predecessor). If any ancestor has higher decayed confidence, raise the current
    /// belief's effective confidence to that level.
    /// When the anchor fires, sets AnchorRawConfidence, AnchorPredecessorId, and
    /// AnchorPredecessorTier annotation fields for provenance.
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
            // Walk the full supersession chain for this (Dimension, Criterion) slot.
            // Stopping at the direct predecessor misses the Verified ancestor after a
            // second consecutive Reported supersession, collapsing ConfidenceFloor
            // below the 0.60 Critical gate incorrectly.
            var ancestors = new List<(Belief belief, double conf)>();
            var toExpand  = new Queue<Guid>();
            toExpand.Enqueue(b.Id);

            while (toExpand.Count > 0)
            {
                var parentId = toExpand.Dequeue();
                foreach (var a in allHistory
                    .Where(x => x.SupersededBy == parentId
                             && x.Dimension    == b.Dimension
                             && x.Criterion    == b.Criterion))
                {
                    ancestors.Add((a, _decay.WithCurrentFreshness(a, _profile, now).Confidence));
                    toExpand.Enqueue(a.Id);
                }
            }

            if (ancestors.Count == 0)
            {
                result.Add(b);
                continue;
            }

            var best = ancestors.MaxBy(x => x.conf);
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
        var contradictions = new List<Contradiction>(
            ContradictionDetector.DetectSupersession(
                entityId.ToString(), decayed, allHistory, _profile.ClaimKeyCatalogue));
        contradictions.AddRange(
            ContradictionDetector.DetectCrossSource(
                entityId.ToString(), decayed, _profile.ClaimKeyCatalogue));

        var anchorNotes = new List<string>();

        // Gap detection: dimensions with no current evidence
        var gaps        = new List<Gap>();
        var coveredDims = decayed.Where(b => b.Dimension.HasValue).Select(b => b.Dimension!.Value).ToHashSet();
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
                    $"{orig.Confidence:F3}→{anch.Confidence:F3} " +
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
        var byDim = decayed
            .Where(b => b.Dimension.HasValue)
            .GroupBy(b => b.Dimension!.Value);
        return byDim.ToDictionary(
            g => g.Key,
            g => _rubric.ScoreDimension(entityId, g.Key, g.ToList(), _profile));
    }

    private ManagementBlock ComputeManagementBlock(
        Guid                                           entityId,
        IReadOnlyList<Belief>                          decayed,
        IReadOnlyList<Belief>                          allBeliefs,
        IReadOnlyDictionary<Dimension, DimensionScore> dimScores,
        MetaCognitionResult                            meta)
    {
        var compResult = new CompletenessService(_profile).Compute(entityId, allBeliefs);

        var weakDims = dimScores
            .Where(kv => kv.Value.ContributingBeliefIds.Count > 0
                      && kv.Value.Score < _profile.Bands.AtRiskMin)
            .Select(kv => kv.Key)
            .OrderBy(d => d.ToString())
            .ToList<Dimension>();

        var renewalDateBelief  = allBeliefs.FirstOrDefault(b => b.ClaimKey == "renewal_date");
        var noticePeriodBelief = allBeliefs.FirstOrDefault(b => b.ClaimKey == "notice_period");
        DateTimeOffset? renewalDeadline = null;
        if (renewalDateBelief != null && noticePeriodBelief != null)
        {
            var rd         = DateTimeOffset.FromUnixTimeSeconds((long)renewalDateBelief.Value);
            var noticeDays = (int)Math.Round(noticePeriodBelief.Value);
            renewalDeadline = rd.AddDays(-noticeDays);
        }

        var flags = new ManagementFlags(
            RenewalDeadline:   renewalDeadline,
            HasContradictions: meta.Contradictions.Count > 0);

        var verState = decayed.Any(b => b.SourceTier is SourceTier.Verified
                                                       or SourceTier.Reported
                                                       or SourceTier.Primary)
            ? VerificationState.PartiallyVerified
            : VerificationState.Unverified;

        var nextDue = decayed
            .Where(b => b.ValidUntil.HasValue)
            .OrderBy(b => b.ValidUntil!.Value)
            .Select(b => b.ValidUntil)
            .FirstOrDefault();

        var filledStr = compResult.FilledKeys.Count > 0
            ? string.Join(", ", compResult.FilledKeys) : "none";
        var gapStr = compResult.GapKeys.Count > 0
            ? string.Join(", ", compResult.GapKeys) : "none";

        return new ManagementBlock(
            Completeness:      compResult.Ratio,
            FilledCount:       compResult.FilledKeys.Count,
            ExpectedCount:     compResult.FilledKeys.Count + compResult.GapKeys.Count,
            GapSlots:          compResult.GapKeys,
            WeakDimensions:    weakDims,
            Flags:             flags,
            VerificationState: verState,
            Refresh:           new RefreshInfo(nextDue),
            CoverageStatement: $"Confident in: {filledStr}. Gaps: {gapStr}.");
    }
}
