using Ii.Contracts;
using Ii.Spine;
using Kozmo.Contracts;
using Rm.Contracts;
using Wc.Contracts;

namespace Rm.Query;

/// <summary>
/// Step 2 of the query pipeline: deterministic retrieval from the existing read model.
/// No LLM. No writes. Aspect controls which sections are populated.
/// FilterDimension (optional) narrows beliefs, gaps, and contradictions to one dimension.
///
/// Seam for future access control: callerContext is threaded through but never acted on.
/// </summary>
public sealed class VendorQueryRetriever
{
    private readonly IIiFacade      _facade;
    private readonly ICheckInStore  _checkInStore;
    private readonly EntityRegistry _registry;

    public VendorQueryRetriever(
        IIiFacade      facade,
        ICheckInStore  checkInStore,
        EntityRegistry registry)
    {
        _facade       = facade;
        _checkInStore = checkInStore;
        _registry     = registry;
    }

    /// <summary>
    /// Pull a RetrievedContext for the given vendor, aspect, and optional dimension filter.
    /// Returns null if the vendor ID is not in the registry (completely unknown entity).
    /// Returns a context with IsAssessed=false if the vendor is known but has no scored data.
    /// When filterDimension is set, beliefs/gaps/contradictions are scoped to that dimension only.
    /// callerContext is a no-op seam â€” reserved for future authorization.
    /// </summary>
    public async Task<RetrievedContext?> RetrieveAsync(
        Guid       vendorId,
        Aspect     aspect,
        Dimension? filterDimension = null,
        string?    callerContext   = null,
        CancellationToken ct       = default)
    {
        var entity = _registry.GetEntity(vendorId);
        if (entity is null) return null;

        // Single round-trip for posture + index + beliefs + meta
        var trail = await _facade.GetReasoningTrailAsync(vendorId, ct);

        bool isAssessed = trail?.Posture is not null && trail?.Index is not null;

        // Open check-ins for this vendor (gaps awaiting owner response)
        IReadOnlyList<OpenCheckInSummary> openCheckIns = [];
        if (isAssessed && aspect is Aspect.Full or Aspect.Gaps)
        {
            var allOpen = await _checkInStore.GetOpenAsync(ct);
            openCheckIns = allOpen
                .Where(c => c.VendorId == vendorId)
                .Select(c => new OpenCheckInSummary(c.CheckInId, c.Question, c.Kind.ToString()))
                .ToList();
        }

        // Raw collections from trail
        var allBeliefs        = trail?.CurrentBeliefs ?? [];
        var allContradictions = trail?.Meta?.Contradictions ?? [];
        var allGaps           = trail?.Meta?.Gaps ?? [];

        // Apply dimension filter when specified
        IReadOnlyList<Belief>        beliefs;
        IReadOnlyList<Contradiction> contradictions;
        IReadOnlyList<Gap>           gaps;

        if (filterDimension is not null)
        {
            var dimName = filterDimension.Value.ToString();
            beliefs        = allBeliefs.Where(b => b.Dimension == filterDimension).ToList();
            contradictions = allContradictions.Where(c => c.Dimension == dimName).ToList();
            gaps           = allGaps.Where(g => g.Dimension == dimName).ToList();
        }
        else
        {
            beliefs = aspect is Aspect.Full or Aspect.Evidence
                ? allBeliefs : [];
            contradictions = aspect is Aspect.Full or Aspect.Contradictions
                ? allContradictions : [];
            gaps = aspect is Aspect.Full or Aspect.Gaps
                ? allGaps : [];
        }

        return new RetrievedContext(
            VendorId:         vendorId,
            VendorName:       entity.CanonicalName,
            IsAssessed:       isAssessed,
            Posture:          trail?.Posture,
            Index:            trail?.Index,
            Beliefs:          beliefs,
            Contradictions:   contradictions,
            Gaps:             gaps,
            EpistemicSummary: trail?.Meta?.EpistemicSummary,
            OpenCheckIns:     openCheckIns,
            CallerContext:    callerContext,
            FilterDimension:  filterDimension
        );
    }
}