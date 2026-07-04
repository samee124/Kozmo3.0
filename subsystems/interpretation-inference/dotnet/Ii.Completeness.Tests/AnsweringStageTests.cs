using System.Text.Json;
using Ii.Completeness;
using Kozmo.Contracts;
using Kozmo.Llm;
using Xunit;

namespace Ii.Completeness.Tests;

public sealed class AnsweringStageTests
{
    private static readonly DateTimeOffset AnchorNow =
        new(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly string CassettePath =
        FindFixture("fixtures", "completeness", "answering.cassette.json");

    private static IReadOnlyList<Question> L1Questions =>
        QuestionSelector.Select(SaasQuestionBank.Category, DepthLevel.L1);

    // ── Unit tests (FakeLlmClient — no cassette required) ───────────────────

    [Fact]
    public async Task No_evidence_answer_is_UNKNOWN_with_low_confidence()
    {
        // The stage passes zero beliefs → user prompt contains "0 item(s)" →
        // the fake LLM returns UNKNOWN/0.10, which the stage must preserve.
        var unknownJson = """
            {
              "answer": "UNKNOWN",
              "confidence": 0.10,
              "cited_belief_ids": [],
              "reasoning": "No supporting evidence found in the available beliefs."
            }
            """;
        var fake  = new FakeLlm(unknownJson);
        var stage = new QuestionAnsweringStage(fake);
        var q     = L1Questions.First(q => q.Dimension == Dimension.Operational);

        var answers = await stage.AnswerAsync(
            Guid.NewGuid(), [q], beliefs: [], AnchorNow);

        var answer = Assert.Single(answers);
        Assert.Equal("UNKNOWN", answer.Value.ToUpperInvariant());
        Assert.True(answer.Confidence <= 0.30,
            $"UNKNOWN answer confidence {answer.Confidence} must be ≤ 0.30");
        Assert.Empty(answer.CitedBeliefIds);
    }

    [Fact]
    public async Task Stage_clamps_confidence_when_model_marks_UNKNOWN_but_reports_high_confidence()
    {
        // Guard against a model that says UNKNOWN but forgets to lower confidence.
        var json = """
            {
              "answer": "UNKNOWN",
              "confidence": 0.95,
              "cited_belief_ids": [],
              "reasoning": "No evidence."
            }
            """;
        var fake  = new FakeLlm(json);
        var stage = new QuestionAnsweringStage(fake);
        var q     = L1Questions.First();

        var answers = await stage.AnswerAsync(Guid.NewGuid(), [q], [], AnchorNow);

        Assert.True(answers[0].Confidence <= 0.30,
            "Stage must clamp UNKNOWN confidence to ≤ 0.30 regardless of what the model returns.");
    }

    [Fact]
    public async Task Grounded_answer_preserves_cited_belief_ids()
    {
        // cited_belief_ids now carries a small ordinal (see AnsweringPrompt.SerializeBeliefs),
        // not the belief's real Guid — a single-belief list makes that ordinal unambiguously "1"
        // without needing to hand-sort a larger fixture by (Criterion, Derivation) to predict it.
        var beliefId = Guid.Parse("b1000001-0000-0000-0000-000000000001");
        var belief   = FixtureBeliefs.Iivs.Single(b => b.Id == beliefId);
        var json = """
            {
              "answer": "YES",
              "confidence": 0.90,
              "cited_belief_ids": ["1"],
              "reasoning": "MSA Section 3.1 documents a 99.9% uptime SLA."
            }
            """;
        var fake  = new FakeLlm(json);
        var stage = new QuestionAnsweringStage(fake);
        var q     = L1Questions.First(q => q.Dimension == Dimension.Operational);

        var answers = await stage.AnswerAsync(
            Guid.NewGuid(), [q], [belief], AnchorNow);

        var answer = Assert.Single(answers);
        Assert.Equal("YES", answer.Value.ToUpperInvariant());
        Assert.Equal(0.90, answer.Confidence, precision: 10);
        Assert.Contains(beliefId, answer.CitedBeliefIds);
    }

    [Fact]
    public async Task Malformed_LLM_response_produces_UNKNOWN_not_crash()
    {
        var fake  = new FakeLlm("not valid json at all");
        var stage = new QuestionAnsweringStage(fake);
        var q     = L1Questions.First();

        // Must not throw — parse failure → UNKNOWN / low confidence.
        var answers = await stage.AnswerAsync(Guid.NewGuid(), [q], [], AnchorNow);

        var answer = Assert.Single(answers);
        Assert.Equal("UNKNOWN", answer.Value.ToUpperInvariant());
        Assert.True(answer.Confidence <= 0.30);
    }

    [Fact]
    public async Task AnsweredAt_reflects_supplied_now_not_system_clock()
    {
        var specificNow = new DateTimeOffset(2025, 6, 15, 12, 0, 0, TimeSpan.Zero);
        var json        = """{"answer":"YES","confidence":0.80,"cited_belief_ids":[],"reasoning":"ok"}""";
        var fake        = new FakeLlm(json);
        var stage       = new QuestionAnsweringStage(fake);
        var q           = L1Questions.First();

        var answers = await stage.AnswerAsync(Guid.NewGuid(), [q], [], specificNow);

        Assert.Equal(specificNow, answers[0].AnsweredAt);
    }

    [Fact]
    public async Task Questions_processed_in_stable_id_order_independent_of_input_order()
    {
        // Reversal of the question list must not change the call order — the stage sorts by Id.
        var callOrder = new List<string>();
        var fake      = new FakeLlm(
            """{"answer":"YES","confidence":0.80,"cited_belief_ids":[],"reasoning":"ok"}""",
            onCall: (user) => callOrder.Add(ExtractQuestionId(user)));
        var stage     = new QuestionAnsweringStage(fake);

        var questions = L1Questions.Reverse().ToList(); // deliberately reversed
        await stage.AnswerAsync(Guid.NewGuid(), questions, [], AnchorNow);

        var sortedIds = L1Questions.Select(q => q.Id).OrderBy(id => id, StringComparer.Ordinal).ToList();
        Assert.Equal(sortedIds, callOrder);
    }

    // ── Integration tests (cassette-backed) ─────────────────────────────────
    // These tests require tools/Kozmo.CompletenessRecorder to have been run with
    // OPENAI_API_KEY to populate fixtures/completeness/answering.cassette.json.
    // Pre-recording they pass trivially with a diagnostic message; post-recording
    // they perform the real two-sided completeness assertion.

    [SkippableFact]
    public async Task IIVS_rich_vendor_answers_produce_high_coverage_at_L1()
    {
        Skip.If(!CassetteHasEntries(),
            "Cassette not yet recorded — run: dotnet run --project tools/Kozmo.CompletenessRecorder");

        var stage     = new QuestionAnsweringStage(new CachingLlmClient(CassettePath));
        var questions = L1Questions;

        var answers = await stage.AnswerAsync(
            FixtureBeliefs.IivsVendorId, questions, FixtureBeliefs.Iivs, AnchorNow);

        var profile = CompletenessRubric.Compute(questions, answers);

        // IIVS has rich evidence covering all four L1 dimensions — expect at least 50% coverage.
        // The exact threshold is conservative: the two-sided assertion (below) is the real check.
        Assert.True(profile.OverallCoverage >= 0.50,
            $"IIVS (rich) overall coverage {profile.OverallCoverage:P0} is unexpectedly low. " +
            $"Answered: [{string.Join(", ", profile.AnsweredQuestionIds)}]. " +
            $"Gaps: [{string.Join(", ", profile.GapQuestionIds)}].");

        // At least one grounded answer should cite a belief ID.
        var hasGroundedAnswer = answers.Any(a => a.CitedBeliefIds.Count > 0);
        Assert.True(hasGroundedAnswer,
            "Expected at least one answer from IIVS to cite a belief ID — none did.");
    }

    [SkippableFact]
    public async Task Regulus_sparse_vendor_has_zero_operational_experiential_strategic_coverage()
    {
        Skip.If(!CassetteHasEntries(),
            "Cassette not yet recorded — run: dotnet run --project tools/Kozmo.CompletenessRecorder");

        var stage     = new QuestionAnsweringStage(new CachingLlmClient(CassettePath));
        var questions = L1Questions;

        var answers = await stage.AnswerAsync(
            FixtureBeliefs.RegulusVendorId, questions, FixtureBeliefs.Regulus, AnchorNow);

        var profile = CompletenessRubric.Compute(questions, answers);

        // Regulus has only Financial beliefs → Op/Exp/Str must be 0% coverage (all gaps).
        foreach (var dim in new[] { Dimension.Operational, Dimension.Experiential, Dimension.Strategic })
        {
            var cov = profile.DimensionCoverages.Single(d => d.Dimension == dim);
            Assert.True(cov.AnsweredCount == 0,
                $"Regulus (sparse) dimension {dim} should have 0 answered questions " +
                $"(no evidence), but got {cov.AnsweredCount}/{cov.RequiredCount}.");
        }

        // Regulus UNKNOWN answers must have low confidence — not hallucinated.
        var unknownAnswers = answers.Where(a => a.Value.Equals("UNKNOWN", StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var a in unknownAnswers)
            Assert.True(a.Confidence <= 0.30,
                $"UNKNOWN answer for '{a.QuestionId}' has confidence {a.Confidence} — must be ≤ 0.30.");
    }

    [SkippableFact]
    public async Task Real_LLM_answers_UNKNOWN_for_no_evidence_Strategic_question_against_Regulus()
    {
        // THE critical grounding test. FakeLlm proves the stage routes correctly; this proves the
        // real model actually says "can't answer from evidence" rather than fabricating completeness
        // when the belief set contains no information for the question's dimension.
        //
        // Regulus has ONLY Financial beliefs. saas.str.l1.1 asks about strategic roadmap alignment.
        // The real model (via cassette) must return "UNKNOWN" / confidence ≤ 0.30.
        // Any fabricated positive/negative answer proves hallucination — the test fails loudly.
        Skip.If(!CassetteHasEntries(),
            "Cassette not yet recorded — run: dotnet run --project tools/Kozmo.CompletenessRecorder");

        var stage = new QuestionAnsweringStage(new CachingLlmClient(CassettePath));

        // Ask exactly one no-evidence question: a Strategic L1 question against Financial-only beliefs.
        var strategicQ = QuestionSelector.Select(SaasQuestionBank.Category, DepthLevel.L1)
            .First(q => q.Dimension == Dimension.Strategic);

        var answers = await stage.AnswerAsync(
            FixtureBeliefs.RegulusVendorId, [strategicQ], FixtureBeliefs.Regulus, AnchorNow);

        var answer = Assert.Single(answers);

        Assert.True(answer.Value.Equals("UNKNOWN", StringComparison.OrdinalIgnoreCase),
            $"Real LLM returned '{answer.Value}' (conf={answer.Confidence:F2}) for Strategic question " +
            $"'{strategicQ.Id}' against Regulus (Financial-only beliefs). " +
            "Expected UNKNOWN — this indicates hallucination: the model fabricated an answer " +
            "from zero evidence rather than admitting it cannot answer.");

        Assert.True(answer.Confidence <= 0.30,
            $"Real LLM answered UNKNOWN but reported confidence {answer.Confidence:F2} — must be ≤ 0.30.");

        Assert.True(answer.CitedBeliefIds.Count == 0,
            $"Real LLM cited belief IDs {string.Join(", ", answer.CitedBeliefIds)} for a Strategic " +
            "question against Financial-only beliefs — no strategic belief exists to cite.");
    }

    [SkippableFact]
    public async Task Two_sided_IIVS_coverage_exceeds_Regulus_coverage()
    {
        Skip.If(!CassetteHasEntries(),
            "Cassette not yet recorded — run: dotnet run --project tools/Kozmo.CompletenessRecorder");

        var stage     = new QuestionAnsweringStage(new CachingLlmClient(CassettePath));
        var questions = L1Questions;

        var iivsAnswers    = await stage.AnswerAsync(FixtureBeliefs.IivsVendorId,    questions, FixtureBeliefs.Iivs,    AnchorNow);
        var regulusAnswers = await stage.AnswerAsync(FixtureBeliefs.RegulusVendorId, questions, FixtureBeliefs.Regulus, AnchorNow);

        var iivsProfile    = CompletenessRubric.Compute(questions, iivsAnswers);
        var regulusProfile = CompletenessRubric.Compute(questions, regulusAnswers);

        Assert.True(iivsProfile.OverallCoverage > regulusProfile.OverallCoverage,
            $"Two-sided check failed: IIVS coverage {iivsProfile.OverallCoverage:P0} " +
            $"is not greater than Regulus coverage {regulusProfile.OverallCoverage:P0}. " +
            "Rich vendor must outperform sparse vendor.");
    }

    [SkippableFact]
    public async Task Cassette_answers_replay_deterministically()
    {
        Skip.If(!CassetteHasEntries(),
            "Cassette not yet recorded — run: dotnet run --project tools/Kozmo.CompletenessRecorder");

        var stage     = new QuestionAnsweringStage(new CachingLlmClient(CassettePath));
        var questions = L1Questions;

        var first  = await stage.AnswerAsync(FixtureBeliefs.IivsVendorId, questions, FixtureBeliefs.Iivs, AnchorNow);
        var second = await stage.AnswerAsync(FixtureBeliefs.IivsVendorId, questions, FixtureBeliefs.Iivs, AnchorNow);

        Assert.Equal(first.Count, second.Count);
        for (var i = 0; i < first.Count; i++)
        {
            Assert.Equal(first[i].QuestionId, second[i].QuestionId);
            Assert.Equal(first[i].Value,      second[i].Value);
            Assert.Equal(first[i].Confidence, second[i].Confidence);
        }
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static bool CassetteHasEntries()
    {
        if (!File.Exists(CassettePath)) return false;
        var json = File.ReadAllText(CassettePath).Trim();
        return json.Length > 2; // "{}" or empty = not yet recorded
    }

    private static string FindFixture(params string[] segments)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (dir.GetFiles("Kozmo.sln").Length > 0)
                return Path.Combine([dir.FullName, .. segments]);
            dir = dir.Parent;
        }
        throw new InvalidOperationException(
            $"Cannot find fixture '{string.Join("/", segments)}' walking up from {AppContext.BaseDirectory}");
    }

    private static string ExtractQuestionId(string userPrompt)
    {
        // "Question (id: saas.op.l1.1, type: ...)" — extract the id value
        var start = userPrompt.IndexOf("id: ", StringComparison.Ordinal) + 4;
        var end   = userPrompt.IndexOf(",", start, StringComparison.Ordinal);
        return end > start ? userPrompt[start..end] : userPrompt;
    }
}

// ── Minimal fake LLM for unit tests ─────────────────────────────────────────

file sealed class FakeLlm : IKozmoLlm
{
    private readonly string           _responseJson;
    private readonly Action<string>?  _onCall;

    public FakeLlm(string responseJson, Action<string>? onCall = null)
    {
        _responseJson = responseJson;
        _onCall       = onCall;
    }

    public Task<LlmResult> CompleteJsonAsync(
        string system, string user, int maxTokens = 500, CancellationToken ct = default)
    {
        _onCall?.Invoke(user);
        try
        {
            var el = JsonSerializer.Deserialize<JsonElement>(_responseJson);
            return Task.FromResult(new LlmResult(el, 0.85, "fake"));
        }
        catch
        {
            // Return a result whose Answer is a string, triggering parse failure in the stage.
            var el = JsonSerializer.Deserialize<JsonElement>($"\"{_responseJson}\"");
            return Task.FromResult(new LlmResult(el, 0.85, "fake"));
        }
    }

    public Task<LlmResult> CompleteVisionAsync(
        string system, byte[] imageBytes, int maxTokens = 500, CancellationToken ct = default) =>
        throw new NotSupportedException("FakeLlm does not support vision.");
}
