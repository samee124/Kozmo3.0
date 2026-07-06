using Kozmo.Contracts;
using Wc.Contracts;

namespace Ii.Completeness;

/// <summary>
/// Orchestrates the Q&amp;A completeness convergence loop for a vendor (Phase 5, Commit 3).
/// Plugged into IiFacade.RecomputeVendorAsync as an optional hook so the recompute path
/// automatically advances completeness whenever beliefs change.
///
/// Convergence flow per call:
///   current beliefs → answer all selected questions (cassette-backed LLM) →
///   CompletenessRubric → permanent-gap promotion → GapCheckInStage.
///
/// Permanent-gap semantics (lifecycle-based, not recompute-count-based):
/// A gap question is promoted to PERMANENT when its check-in has been RESOLVED (PROCESSED
/// or EXPIRED) and the question is STILL a gap — meaning "we asked and got no answer."
/// Unrelated RecomputeVendorAsync calls do not advance this counter; only a resolved
/// check-in lifecycle event triggers promotion. Once permanent, the question is never
/// re-raised as a check-in; it remains in the profile as an honest gap.
///
/// Does NOT read the clock — caller (IiFacade) supplies 'now' from the wall clock.
/// </summary>
public sealed class CompletenessOrchestrator
{
    private readonly QuestionAnsweringStage _answering;
    private readonly GapCheckInStage        _gapStage;
    private readonly ICheckInStore          _checkInStore;
    private readonly DepthLevel             _maxDepth;
    private readonly string                 _owner;
    private readonly ICheckInTransport?     _transport;

    // Permanent gap state — in-memory for Phase 0; resets on process restart (one extra re-nag per gap per restart, acceptable).
    private readonly Dictionary<Guid, HashSet<string>> _permanentGapSets = [];

    /// <param name="transport">
    /// Optional — forwarded to GapCheckInStage.RaiseAsync so a newly raised check-in is also sent
    /// through a real channel (e.g. email). Defaults to null (in-app only, the prior behavior);
    /// every existing caller that doesn't pass one is unaffected.
    /// </param>
    public CompletenessOrchestrator(
        QuestionAnsweringStage answering,
        GapCheckInStage        gapStage,
        ICheckInStore          checkInStore,
        DepthLevel             maxDepth,
        string                 owner,
        ICheckInTransport?     transport = null)
    {
        _answering    = answering;
        _gapStage     = gapStage;
        _checkInStore = checkInStore;
        _maxDepth     = maxDepth;
        _owner        = owner;
        _transport    = transport;
    }

    /// <summary>
    /// Re-answers all selected questions from the current belief set, recomputes the
    /// completeness profile, advances permanent-gap state, and raises check-ins for
    /// new non-permanent gaps. Returns the recomputed CompletenessProfile.
    /// </summary>
    public async Task<CompletenessProfile> RunAsync(
        Guid                  vendorId,
        IReadOnlyList<Belief> beliefs,
        DateTimeOffset        now,
        CancellationToken     ct = default)
    {
        var questions = QuestionSelector.Select(SaasQuestionBank.Category, _maxDepth);
        var answers   = await _answering.AnswerAsync(vendorId, questions, beliefs, now, ct);
        var profile   = CompletenessRubric.Compute(questions, answers);

        // Permanent-gap promotion: a gap is permanent when its DIMENSION_GAP check-in has
        // been RESOLVED (PROCESSED or EXPIRED) and the question is still a gap now.
        // This ties promotion to "we asked and got no answer," not to recompute count.
        if (!_permanentGapSets.TryGetValue(vendorId, out var permanent))
            _permanentGapSets[vendorId] = permanent = [];

        var resolvedCheckIns = await _checkInStore.GetResolvedForVendorAsync(vendorId, ct);
        var resolvedQuestionIds = resolvedCheckIns
            .Where(c => c.Kind == CheckInKind.DIMENSION_GAP && c.TargetField != null)
            .Select(c => c.TargetField!)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var gapId in profile.GapQuestionIds)
        {
            if (resolvedQuestionIds.Contains(gapId))
                permanent.Add(gapId);
        }

        await _gapStage.RaiseAsync(
            vendorId,
            profile.GapQuestionIds,
            questions,
            permanent,
            _checkInStore,
            _owner,
            Guid.NewGuid(),
            now,
            ct,
            _transport);

        return profile;
    }

    /// <summary>
    /// Returns the current permanent gap set for a vendor.
    /// A question ID in this set was a gap in at least two consecutive cycles.
    /// Exposed for test visibility.
    /// </summary>
    public IReadOnlySet<string> GetPermanentGaps(Guid vendorId) =>
        _permanentGapSets.TryGetValue(vendorId, out var s)
            ? s
            : (IReadOnlySet<string>)new HashSet<string>(StringComparer.Ordinal);
}
