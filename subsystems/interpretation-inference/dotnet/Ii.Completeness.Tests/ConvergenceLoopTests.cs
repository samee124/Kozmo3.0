using System.Text.Json;
using Ii.Completeness;
using Kozmo.Contracts;
using Kozmo.Llm;
using Wc.Contracts;
using Xunit;

namespace Ii.Completeness.Tests;

using CheckIn = global::Wc.Contracts.CheckIn;

/// <summary>
/// Convergence loop and termination tests for CompletenessOrchestrator (Phase 5, Commit 3).
///
/// Termination properties under test:
///   (a) An answered question does NOT re-fire as a check-in.
///   (b) An unanswerable gap (present in two consecutive cycles) becomes a permanent gap
///       and is NOT re-raised.
///   (c) Depth ladder caps which questions fire: L1-only orchestrator raises no L2/L3 gaps.
/// </summary>
public sealed class ConvergenceLoopTests
{
    private static readonly DateTimeOffset AnchorNow =
        new(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly Guid VendorId =
        Guid.Parse("cccccccc-0000-0000-0000-000000000001");

    // ── Termination (a): answered questions never re-fire across cycles ────────
    //
    // This is the CROSS-CYCLE proof, not just a within-pass check.
    // Within-pass: "high-confidence answer → not in GapQuestionIds" is trivially enforced
    // by CompletenessRubric. The loop-convergence property requires a two-cycle run:
    //   Cycle 1 — Q1 answered, Q2..QN gap → check-ins raised for gaps, none for Q1.
    //   Cycle 2 — Q1 still answered, Q2..QN still UNKNOWN → de-dup skips existing OPEN
    //             check-ins for gaps; Q1 never appears because it is never in GapQuestionIds.
    // Assert: after cycle 2, Q1 has zero check-ins and gap count is unchanged.

    [Fact]
    public async Task Answered_question_in_cycle1_never_raises_check_in_in_cycle2()
    {
        // L1 = 8 questions, sorted by Id. First question → YES (high conf). Rest → UNKNOWN.
        var questions  = QuestionSelector.Select(SaasQuestionBank.Category, DepthLevel.L1);
        var answeredId = questions.OrderBy(q => q.Id, StringComparer.Ordinal).First().Id;
        var gapCount   = questions.Count - 1;   // 7 gaps expected

        var store = new ConvergenceCheckInStore();
        var orch  = BuildOrchestrator(
            new SelectiveFakeLlm(answeredId, HighConfidenceYes, UnknownAnswer), store, DepthLevel.L1);

        // ── Cycle 1 ──────────────────────────────────────────────────────────
        await orch.RunAsync(VendorId, [], AnchorNow);
        var afterCycle1 = await store.GetOpenAsync();

        // Answered question has NO check-in.
        Assert.True(afterCycle1.All(ci => ci.TargetField != answeredId),
            $"Cycle 1: answered question '{answeredId}' must not have a check-in.");

        // The 7 gap questions each have exactly one check-in.
        Assert.Equal(gapCount, afterCycle1.Count);

        // ── Cycle 2 — same answers; answered question still answered ─────────
        await orch.RunAsync(VendorId, [], AnchorNow);
        var afterCycle2 = await store.GetOpenAsync();

        // Cross-cycle: answered question STILL has no check-in.
        Assert.True(afterCycle2.All(ci => ci.TargetField != answeredId),
            $"Cycle 2: answered question '{answeredId}' must not have a check-in " +
            "(cross-cycle termination: answered does not re-fire).");

        // Gap check-in count is unchanged — de-dup prevents re-raising the same 7.
        Assert.Equal(afterCycle1.Count, afterCycle2.Count);
    }

    [Fact]
    public async Task Partial_coverage_raises_check_ins_only_for_gaps()
    {
        // FakeLlm returns UNKNOWN for all questions → all 8 L1 questions become gaps.
        var store = new ConvergenceCheckInStore();
        var orch  = BuildOrchestrator(UnknownAnswer, store, DepthLevel.L1);

        var profile = await orch.RunAsync(VendorId, [], AnchorNow);

        var l1Count = QuestionSelector.Select(SaasQuestionBank.Category, DepthLevel.L1).Count;
        Assert.Equal(l1Count, profile.GapQuestionIds.Count);
        Assert.Equal(l1Count, (await store.GetOpenAsync()).Count);

        // check-in ≠ action: all are DIMENSION_GAP
        Assert.All(await store.GetOpenAsync(),
            ci => Assert.Equal(CheckInKind.DIMENSION_GAP, ci.Kind));
    }

    // ── Termination (b): permanent gaps are not re-raised ─────────────────────
    //
    // Lifecycle semantics: a gap becomes permanent when its check-in is RESOLVED
    // (PROCESSED or EXPIRED) and the question is still a gap. This is "we asked and got
    // no answer," not "we recomputed twice." The test simulates:
    //   Cycle 1 — UNKNOWN → check-in raised (OPEN)
    //   Human resolves — mark check-in PROCESSED (simulates ProcessCheckInService)
    //   Cycle 2 — still UNKNOWN → resolved check-in detected → gap promoted to permanent
    //   Assert: no new check-ins raised for those gaps; all L1 gaps are permanent.

    [Fact]
    public async Task Gap_with_resolved_check_in_is_promoted_to_permanent_and_not_re_raised()
    {
        var store = new ConvergenceCheckInStore();
        var orch  = BuildOrchestrator(UnknownAnswer, store, DepthLevel.L1);

        // Cycle 1: all UNKNOWN → 8 gaps → 8 OPEN check-ins raised.
        await orch.RunAsync(VendorId, [], AnchorNow);
        var afterCycle1 = await store.GetOpenAsync();
        Assert.True(afterCycle1.Count > 0, "Cycle 1 must raise at least one check-in.");

        // Simulate human resolving all gap check-ins (ProcessCheckInService marks PROCESSED).
        foreach (var ci in afterCycle1.Where(c => c.Kind == CheckInKind.DIMENSION_GAP))
            await store.SaveAsync(ci with { Status = PendingStatus.PROCESSED });

        // No OPEN check-ins remain before cycle 2.
        Assert.Empty(await store.GetOpenAsync());

        // Cycle 2: still UNKNOWN → resolved check-ins detected → all gaps promoted to permanent → no new check-ins.
        await orch.RunAsync(VendorId, [], AnchorNow);
        Assert.Empty(await store.GetOpenAsync());

        // All L1 gaps are now permanent.
        var l1Ids = QuestionSelector.Select(SaasQuestionBank.Category, DepthLevel.L1)
            .Select(q => q.Id).ToHashSet(StringComparer.Ordinal);
        var perm = orch.GetPermanentGaps(VendorId);
        Assert.True(l1Ids.IsSubsetOf(perm),
            "All L1 questions should be permanent after their check-ins were resolved and they remained gaps.");
    }

    [Fact]
    public async Task Closing_a_gap_removes_it_from_subsequent_check_in_raises()
    {
        // Cycle 1: UNKNOWN → 8 gaps raised
        // Cycle 2: now answers YES (simulating human filled belief) → 0 gaps → nothing more raised
        var store    = new ConvergenceCheckInStore();
        int callIdx  = 0;
        // First cycle: UNKNOWN; second cycle: high-confidence YES
        var switching = new SwitchingFakeLlm(() => callIdx++ < 8 ? UnknownAnswer : HighConfidenceYes);
        var orch      = BuildOrchestrator(switching, store, DepthLevel.L1);

        await orch.RunAsync(VendorId, [], AnchorNow);  // cycle 1: all UNKNOWN
        var countAfterCycle1 = (await store.GetOpenAsync()).Count;

        await orch.RunAsync(VendorId, [], AnchorNow);  // cycle 2: all answered
        var countAfterCycle2 = (await store.GetOpenAsync()).Count;

        // Cycle 1 raised check-ins; cycle 2 found no gaps → count unchanged
        Assert.Equal(countAfterCycle1, countAfterCycle2);
        Assert.Empty(orch.GetPermanentGaps(VendorId));
    }

    // ── Termination (c): depth ladder caps which questions fire ───────────────

    [Fact]
    public async Task L1_orchestrator_raises_no_L2_or_L3_check_ins()
    {
        var store = new ConvergenceCheckInStore();
        var orch  = BuildOrchestrator(UnknownAnswer, store, DepthLevel.L1);

        await orch.RunAsync(VendorId, [], AnchorNow);

        var open = await store.GetOpenAsync();
        var l2l3Ids = QuestionSelector.Select(SaasQuestionBank.Category, DepthLevel.L3)
            .Where(q => q.DepthLevel != DepthLevel.L1)
            .Select(q => q.Id)
            .ToHashSet(StringComparer.Ordinal);

        Assert.True(open.All(ci => !l2l3Ids.Contains(ci.TargetField ?? "")),
            "L1 orchestrator must not raise check-ins for L2 or L3 questions.");
    }

    [Fact]
    public async Task L3_orchestrator_raises_check_ins_for_all_depth_levels()
    {
        var store = new ConvergenceCheckInStore();
        var orch  = BuildOrchestrator(UnknownAnswer, store, DepthLevel.L3);

        await orch.RunAsync(VendorId, [], AnchorNow);

        var open   = await store.GetOpenAsync();
        var allIds = QuestionSelector.Select(SaasQuestionBank.Category, DepthLevel.L3)
            .Select(q => q.Id).ToHashSet(StringComparer.Ordinal);
        var raised = open.Select(ci => ci.TargetField ?? "").ToHashSet(StringComparer.Ordinal);

        Assert.True(allIds.IsSubsetOf(raised),
            "L3 orchestrator must raise check-ins for all 24 questions (none answered from empty beliefs).");
    }

    // ── check-in ≠ action invariant ──────────────────────────────────────────

    [Fact]
    public async Task Completeness_check_ins_are_all_DIMENSION_GAP_never_IDENTITY_CONFIRM()
    {
        var store = new ConvergenceCheckInStore();
        var orch  = BuildOrchestrator(UnknownAnswer, store, DepthLevel.L1);

        await orch.RunAsync(VendorId, [], AnchorNow);

        Assert.All(await store.GetOpenAsync(),
            ci => Assert.Equal(CheckInKind.DIMENSION_GAP, ci.Kind));
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private const string HighConfidenceYes =
        """{"answer":"YES","confidence":0.95,"cited_belief_ids":[],"reasoning":"ok"}""";

    private const string UnknownAnswer =
        """{"answer":"UNKNOWN","confidence":0.10,"cited_belief_ids":[],"reasoning":"No evidence."}""";

    private static CompletenessOrchestrator BuildOrchestrator(
        string json, ICheckInStore store, DepthLevel depth) =>
        BuildOrchestrator(new ConvergenceFakeLlm(json), store, depth);

    private static CompletenessOrchestrator BuildOrchestrator(
        IKozmoLlm llm, ICheckInStore store, DepthLevel depth) =>
        new(
            new QuestionAnsweringStage(llm),
            new GapCheckInStage(),
            store,
            depth,
            "test@kozmo");
}

// ── Minimal fakes ─────────────────────────────────────────────────────────────

file sealed class ConvergenceFakeLlm(string responseJson) : IKozmoLlm
{
    public Task<LlmResult> CompleteJsonAsync(
        string system, string user, int maxTokens = 500, CancellationToken ct = default)
    {
        try
        {
            var el = JsonSerializer.Deserialize<JsonElement>(responseJson);
            return Task.FromResult(new LlmResult(el, 0.85, "fake"));
        }
        catch
        {
            var el = JsonSerializer.Deserialize<JsonElement>($"\"{responseJson}\"");
            return Task.FromResult(new LlmResult(el, 0.85, "fake"));
        }
    }

    public Task<LlmResult> CompleteVisionAsync(
        string system, byte[] imageBytes, int maxTokens = 500, CancellationToken ct = default) =>
        throw new NotSupportedException();
}

// Answers one specific question with yesJson; all others with unknownJson.
// Uses the "id: <questionId>," prefix in AnsweringPrompt.User to discriminate.
file sealed class SelectiveFakeLlm(string targetId, string yesJson, string unknownJson) : IKozmoLlm
{
    public Task<LlmResult> CompleteJsonAsync(
        string system, string user, int maxTokens = 500, CancellationToken ct = default)
    {
        var start = user.IndexOf("id: ", StringComparison.Ordinal);
        var qId   = "";
        if (start >= 0)
        {
            start += 4;
            var end = user.IndexOf(",", start, StringComparison.Ordinal);
            qId = end > start ? user[start..end].Trim() : "";
        }
        var json = string.Equals(qId, targetId, StringComparison.Ordinal) ? yesJson : unknownJson;
        var el   = JsonSerializer.Deserialize<JsonElement>(json);
        return Task.FromResult(new LlmResult(el, 0.85, "fake"));
    }

    public Task<LlmResult> CompleteVisionAsync(
        string system, byte[] imageBytes, int maxTokens = 500, CancellationToken ct = default) =>
        throw new NotSupportedException();
}

file sealed class SwitchingFakeLlm(Func<string> jsonSelector) : IKozmoLlm
{
    public Task<LlmResult> CompleteJsonAsync(
        string system, string user, int maxTokens = 500, CancellationToken ct = default)
    {
        var json = jsonSelector();
        var el   = JsonSerializer.Deserialize<JsonElement>(json);
        return Task.FromResult(new LlmResult(el, 0.85, "fake"));
    }

    public Task<LlmResult> CompleteVisionAsync(
        string system, byte[] imageBytes, int maxTokens = 500, CancellationToken ct = default) =>
        throw new NotSupportedException();
}

file sealed class ConvergenceCheckInStore : ICheckInStore
{
    private readonly List<CheckIn> _store = [];

    public Task SaveAsync(CheckIn checkIn, CancellationToken ct = default)
    {
        var idx = _store.FindIndex(c => c.CheckInId == checkIn.CheckInId);
        if (idx >= 0) _store[idx] = checkIn;
        else          _store.Add(checkIn);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<CheckIn>> GetOpenAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<CheckIn>>(
            _store.Where(c => c.Status == PendingStatus.OPEN).ToList().AsReadOnly());

    public Task<CheckIn?> GetAsync(Guid checkInId, CancellationToken ct = default) =>
        Task.FromResult(_store.FirstOrDefault(c => c.CheckInId == checkInId));

    public Task<IReadOnlyList<CheckIn>> GetResolvedForVendorAsync(Guid vendorId, CancellationToken ct = default)
    {
        IReadOnlyList<CheckIn> result = _store
            .Where(c => c.VendorId == vendorId &&
                        (c.Status == PendingStatus.PROCESSED || c.Status == PendingStatus.EXPIRED))
            .OrderBy(c => c.RaisedAt)
            .ToList();
        return Task.FromResult(result);
    }
}
