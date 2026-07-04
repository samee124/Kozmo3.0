using System.Text.Json;
using Kozmo.Contracts;
using Kozmo.Llm;
using Xunit;

namespace Ii.Completeness.Tests;

/// <summary>
/// Fix #1 from the demo dry-run plan: AnsweringPrompt used to embed the real, persisted
/// Belief.Id (Guid.NewGuid() at write time) in the prompt sent to the LLM. Two runs over
/// byte-identical evidence therefore produced two different prompts — and two different
/// CachingLlmClient cache keys — purely because the ids happened to differ, which they always
/// do on a genuine run. KyvProgramRunner's own stage 8 proved this: completeness_init was 0 for
/// every vendor, every run.
///
/// The fix: AnsweringPrompt.SerializeBeliefs labels each belief with a small ordinal assigned by
/// a deterministic sort (Criterion, then Derivation — the same order RealVendorBeliefFixture
/// already used for its test-only id remap), not the real Guid. QuestionAnsweringStage keeps an
/// ordinal-to-real-id map for the call and translates cited ordinals back afterward. Belief.Id
/// itself is never touched — Km.Store, persistence, and supersession are untouched by this fix.
/// </summary>
public sealed class BeliefOrdinalStabilityTests
{
    private static readonly DateTimeOffset AnchorNow = new(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void SerializeBeliefs_SameEvidence_DifferentRandomIds_ProducesIdenticalPrompt()
    {
        // THE bug, reproduced directly: two "runs" over the same evidence, each assigning fresh
        // Guid.NewGuid() ids the way VendorFileWriteService does at persistence time.
        var runA = BuildBeliefsWithFreshIds();
        var runB = BuildBeliefsWithFreshIds();

        // Different ids by construction — if this ever failed, the test below would be vacuous.
        Assert.NotEqual(runA.Select(b => b.Id), runB.Select(b => b.Id));

        var question = QuestionSelector.Select(SaasQuestionBank.Category, DepthLevel.L1)
            .First(q => q.Dimension == Dimension.Financial);

        var promptA = AnsweringPrompt.User(question, runA);
        var promptB = AnsweringPrompt.User(question, runB);

        Assert.Equal(promptA, promptB);
    }

    [Fact]
    public async Task Cassette_replay_hits_after_ordinal_fix_despite_fresh_ids()
    {
        // End-to-end proof at the QuestionAnsweringStage level: a FakeLlm keyed by prompt text
        // (not a real cassette) simulates "recorded once, replayed on a run with fresh ids."
        var recordedPrompt = AnsweringPrompt.User(
            QuestionSelector.Select(SaasQuestionBank.Category, DepthLevel.L1)
                .First(q => q.Dimension == Dimension.Financial),
            BuildBeliefsWithFreshIds());

        var replayLlm = new PromptKeyedLlm(recordedPrompt,
            """{"answer":"YES","confidence":0.95,"cited_belief_ids":["1"],"reasoning":"ok"}""");

        var stage    = new QuestionAnsweringStage(replayLlm);
        var question = QuestionSelector.Select(SaasQuestionBank.Category, DepthLevel.L1)
            .First(q => q.Dimension == Dimension.Financial);

        // A genuine "second run" — same evidence shape, brand-new random ids.
        var answers = await stage.AnswerAsync(
            Guid.NewGuid(), [question], BuildBeliefsWithFreshIds(), AnchorNow);

        Assert.True(replayLlm.Matched, "Replay prompt did not match the recorded prompt — the fix did not achieve id-independence.");
        Assert.Equal("YES", Assert.Single(answers).Value);
    }

    [Fact]
    public async Task Citation_ordinal_resolves_to_the_correct_real_belief_id()
    {
        var beliefs = BuildBeliefsWithFreshIds(); // [annual_value, payment_terms] after sort — see below
        var sorted  = AnsweringPrompt.OrderForPrompt(beliefs);

        // Prove the ordinal we're about to cite ("2") really does correspond to the second
        // belief in prompt order — not an assumption about input order.
        var targetOrdinal = 2;
        var expectedId    = sorted[targetOrdinal - 1].Id;

        var fake = new FakeLlmCitingOrdinal(targetOrdinal);
        var stage = new QuestionAnsweringStage(fake);
        var question = QuestionSelector.Select(SaasQuestionBank.Category, DepthLevel.L1)
            .First(q => q.Dimension == Dimension.Financial);

        var answers = await stage.AnswerAsync(Guid.NewGuid(), [question], beliefs, AnchorNow);

        var answer = Assert.Single(answers);
        Assert.Equal(expectedId, Assert.Single(answer.CitedBeliefIds));
    }

    [Fact]
    public async Task Out_of_range_cited_ordinal_is_dropped_not_thrown()
    {
        var beliefs = BuildBeliefsWithFreshIds(); // 2 beliefs -> valid ordinals are 1 and 2 only
        var fake    = new FakeLlmCitingOrdinal(99);
        var stage   = new QuestionAnsweringStage(fake);
        var question = QuestionSelector.Select(SaasQuestionBank.Category, DepthLevel.L1)
            .First(q => q.Dimension == Dimension.Financial);

        var answers = await stage.AnswerAsync(Guid.NewGuid(), [question], beliefs, AnchorNow);

        Assert.Empty(Assert.Single(answers).CitedBeliefIds);
    }

    // ── Fixtures ────────────────────────────────────────────────────────────

    // Mirrors the shape of Salesforce's real structural beliefs (annual_value, payment_terms) —
    // Confidence=0 (structural), Dimension=null, fresh Guid.NewGuid() on every call, exactly as
    // VendorFileWriteService assigns at persistence time on every genuine run.
    private static IReadOnlyList<Belief> BuildBeliefsWithFreshIds() =>
    [
        new Belief(
            Id: Guid.NewGuid(), EntityId: Guid.NewGuid(), Dimension: null,
            Criterion: "annual_value", Value: 214_500, SourceTier: SourceTier.Primary,
            Confidence: 0.0, Freshness: 1.0,
            Derivation: "doc:OrderForm_01_EducationCloud_NPSP_HEDA_NSU_2025_EXECUTED.pdf \"Annual Subscription Fee: $214,500.00 per year.\"",
            SourceSignals: [], Version: 1, SupersededBy: null,
            CreatedAt: AnchorNow, TraceId: Guid.NewGuid()),

        new Belief(
            Id: Guid.NewGuid(), EntityId: Guid.NewGuid(), Dimension: null,
            Criterion: "payment_terms", Value: 30, SourceTier: SourceTier.Primary,
            Confidence: 0.0, Freshness: 1.0,
            Derivation: "doc:OrderForm_01_EducationCloud_NPSP_HEDA_NSU_2025_EXECUTED.pdf \"Invoices are due and payable within thirty (30) days of the invoice date (Net 30)\"",
            SourceSignals: [], Version: 1, SupersededBy: null,
            CreatedAt: AnchorNow, TraceId: Guid.NewGuid()),
    ];

    // ── Fakes ───────────────────────────────────────────────────────────────

    // Simulates cassette replay keyed on exact prompt text — succeeds only if the two "runs"
    // (different random ids) produced byte-identical prompts.
    private sealed class PromptKeyedLlm : IKozmoLlm
    {
        private readonly string _expectedUser;
        private readonly string _responseJson;
        public bool Matched { get; private set; }

        public PromptKeyedLlm(string expectedUser, string responseJson)
        {
            _expectedUser = expectedUser;
            _responseJson = responseJson;
        }

        public Task<LlmResult> CompleteJsonAsync(
            string system, string user, int maxTokens = 500, CancellationToken ct = default)
        {
            if (user != _expectedUser)
                throw new LlmCacheMissException("prompt did not match recorded cassette entry");

            Matched = true;
            var el = JsonSerializer.Deserialize<JsonElement>(_responseJson);
            return Task.FromResult(new LlmResult(el, 0.95, "replay"));
        }
    }

    private sealed class FakeLlmCitingOrdinal : IKozmoLlm
    {
        private readonly int _ordinal;
        public FakeLlmCitingOrdinal(int ordinal) => _ordinal = ordinal;

        public Task<LlmResult> CompleteJsonAsync(
            string system, string user, int maxTokens = 500, CancellationToken ct = default)
        {
            var json = $$"""{"answer":"YES","confidence":0.90,"cited_belief_ids":["{{_ordinal}}"],"reasoning":"ok"}""";
            var el   = JsonSerializer.Deserialize<JsonElement>(json);
            return Task.FromResult(new LlmResult(el, 0.90, "fake"));
        }
    }
}
