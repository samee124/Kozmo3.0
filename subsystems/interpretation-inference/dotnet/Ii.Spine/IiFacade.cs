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

    public IiFacade(
        IObservationModule observation,
        IRubricModule      rubric,
        IIndexModule       index,
        IPostureModule     posture,
        IDecayEngine       decay,
        IEntityStore       store,
        SaasProfile        profile,
        EntityRegistry     registry)
    {
        _observation = observation;
        _rubric      = rubric;
        _index       = index;
        _posture     = posture;
        _decay       = decay;
        _store       = store;
        _profile     = profile;
        _registry    = registry;
    }

    public async Task<Guid> SubmitSignalAsync(Signal signal, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;  // clock read — only here in Spine

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
            TraceId:       signal.TraceId);

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
        var posture  = await _store.GetCurrentPostureAsync(entityId, ct);
        var idx      = await _store.GetIndexAsync(entityId, ct);
        var beliefs  = await _store.GetCurrentBeliefsAsync(entityId, ct);

        var signals = new List<Signal>();
        foreach (var b in beliefs)
        foreach (var sid in b.SourceSignals)
        {
            var sig = await _store.GetSignalAsync(sid, ct);
            if (sig != null) signals.Add(sig);
        }

        return new ReasoningTrail(entityId, posture, idx, beliefs, signals);
    }

    public Task ResetAsync(CancellationToken ct = default) => _store.ResetAsync(ct);

    // ── Private ───────────────────────────────────────────────────────────────

    private async Task RecomputeIndexAsync(Guid entityId, Dimension dirtyDim, DateTimeOffset now, CancellationToken ct)
    {
        var allBeliefs = await _store.GetCurrentBeliefsAsync(entityId, ct);

        // Apply decay to all beliefs before scoring
        var decayed = allBeliefs
            .Select(b => _decay.WithCurrentFreshness(b, _profile, now))
            .ToList();

        var previous = await _store.GetIndexAsync(entityId, ct);

        EntityIndex newIndex;
        if (previous == null)
        {
            // First signal — full aggregate
            var scores = BuildAllDimensionScores(entityId, decayed);
            newIndex = _index.Aggregate(entityId, scores, decayed, null, _profile, now);
        }
        else
        {
            // Incremental — only recompute the dirty dimension
            var dimBeliefs = decayed.Where(b => b.Dimension == dirtyDim).ToList();
            var dimScore   = _rubric.ScoreDimension(entityId, dirtyDim, dimBeliefs, _profile);
            newIndex = _index.RecomputeDirty(entityId, dirtyDim, dimScore, decayed, previous, _profile, now);
        }

        await _store.SaveIndexAsync(newIndex, ct);

        var entity  = _registry.GetEntity(entityId);
        var posture = _posture.Assign(newIndex, previous, entity?.RenewalDate, _profile, now);
        await _store.AppendPostureAsync(posture, ct);
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
