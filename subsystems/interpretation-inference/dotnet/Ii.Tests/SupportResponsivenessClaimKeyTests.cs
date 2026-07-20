using System.Text.Json;
using Ii.CandidateExtraction;
using Ii.Index;
using Ii.Observation;
using Ii.Posture;
using Ii.Rubric;
using Kozmo.Contracts;
using Kozmo.Contracts.Config;
using Kozmo.Llm;
using Xunit;

namespace Ii.Tests;

/// <summary>
/// E-docdepth build item 1 — completes the previously bare <c>support_responsiveness</c> claim key
/// into a real <c>support_response_time</c> extraction, targeted at real evidence found in
/// Brookfield/OfficeSpace's escalation emails (Scenario 07; see KYV_KNOWN_GAPS.md's E-docdepth
/// survey). Covers, end to end: catalogue completeness, extraction against the real email, the
/// observed-vs-committed guard, rubric banding, and the Index/Posture mechanism a persisted belief
/// of this shape would now drive — all offline (fake LLM, direct module calls), no live API call
/// and no cassette re-record required.
/// </summary>
public sealed class SupportResponsivenessClaimKeyTests
{
    private static readonly SaasProfile Profile  = TestHelpers.LoadProfile();
    private static readonly string      Workspace = @"D:\June\Kozmo Workspace";

    // ── Catalogue completeness ──────────────────────────────────────────────────

    [Fact]
    public void CatalogueEntry_IsComplete()
    {
        Assert.True(Profile.ClaimKeyCatalogue.TryGetValue("support_responsiveness", out var def));

        Assert.Equal("scored", def!.ClaimClass);
        Assert.Equal("support_response_time", def.RubricCriterion);
        Assert.False(string.IsNullOrWhiteSpace(def.Definition));
        Assert.False(string.IsNullOrWhiteSpace(def.PositiveExample));
        Assert.False(string.IsNullOrWhiteSpace(def.NegativeExample));
        Assert.False(string.IsNullOrWhiteSpace(def.PromptFragment));
        Assert.Equal("DocumentBeliefExtractor.ContainsFutureCommitmentLanguage", def.DeterministicGuard);

        // rubric_criterion must resolve to a real scoring_rubric.saas.v1.json entry, or banding
        // (BeliefPersistenceStage.BandIfScored -> ObservationModule.ScoreFromRubric) has nothing to
        // band against.
        Assert.True(Profile.ScoringRubric.ContainsKey(def.RubricCriterion!));
    }

    // ── Extraction against the real evidence (email path) ──────────────────────

    [SkippableFact]
    public async Task RealEscalationEmail_ObservedResponseTimeEvidence_Extracts()
    {
        Skip.If(!Directory.Exists(Workspace), $"Workspace absent: '{Workspace}'.");

        var path = Path.Combine(Workspace, "Scenario 07 — Email-Driven Relationship",
            "300 .eml files spanning 3 years", "0128_escalation_13_0_0.eml");
        Skip.If(!File.Exists(path), $"Sample email absent: '{path}'.");

        var email = EmailParser.ParseFile(path);
        Assert.Contains("Repeated Slow Response Times", email.Subject);

        // The real quoted evidence from this email, as a real GPT-4o-mini call would ground it.
        const string json = """
            {"facts":[{"criterion":"support_responsiveness","value":8,"evidence":"each take over 8 hours for an initial response, well beyond what we'd expect","confidence":0.85}],"confidence":0.85,"reasoning":"test"}
            """;
        var extractor = new EmailInterpretationExtractor(new FakeLlm(json), Profile);

        var beliefs = (await extractor.ExtractAsync(email)).Beliefs;

        var belief = Assert.Single(beliefs);
        Assert.Equal("support_responsiveness", belief.Criterion);
        Assert.Equal(8, belief.Value, precision: 6);
        Assert.Equal(Dimension.Operational, belief.Dimension);
        Assert.Equal(SourceTier.Correspondence, belief.SourceTier);
        // Correspondence tier's ceiling (0.25, source_tiers.saas.v1.json) clamps the LLM's own
        // 0.85 self-reported confidence — the safety mechanism that lets email beliefs exist
        // without ever outweighing a document (Kozmo_Phase_E_Signal_Spec.md §3.3).
        Assert.Equal(0.25, belief.Confidence, precision: 6);
    }

    // ── Observed-vs-committed guard ──────────────────────────────────────────────

    [Fact]
    public async Task FutureCommitmentEvidence_IsRejected()
    {
        const string json = """
            {"facts":[{"criterion":"support_responsiveness","value":2,"evidence":"we will guarantee a 2-hour response time going forward","confidence":1.0}],"confidence":1.0,"reasoning":"test"}
            """;
        var extractor = new EmailInterpretationExtractor(new FakeLlm(json), Profile);

        var beliefs = (await extractor.ExtractAsync(SyntheticEmail())).Beliefs;

        Assert.Empty(beliefs);
    }

    [Theory]
    [InlineData("our target is a 4-hour response time")]
    [InlineData("we aim to respond within 4 hours")]
    [InlineData("committed to providing a 4-hour response window moving forward")]
    public async Task OtherFutureCommitmentPhrasings_AreAlsoRejected(string evidence)
    {
        var json = $$"""
            {"facts":[{"criterion":"support_responsiveness","value":4,"evidence":"{{evidence}}","confidence":1.0}],"confidence":1.0,"reasoning":"test"}
            """;
        var extractor = new EmailInterpretationExtractor(new FakeLlm(json), Profile);

        var beliefs = (await extractor.ExtractAsync(SyntheticEmail())).Beliefs;

        Assert.Empty(beliefs);
    }

    [Fact]
    public async Task ObservedEvidenceWithoutCommitmentLanguage_StillPasses_GuardDoesNotOverDrop()
    {
        const string json = """
            {"facts":[{"criterion":"support_responsiveness","value":3,"evidence":"it took us 3 hours to get a first reply on the last ticket","confidence":0.7}],"confidence":0.7,"reasoning":"test"}
            """;
        var extractor = new EmailInterpretationExtractor(new FakeLlm(json), Profile);

        var beliefs = (await extractor.ExtractAsync(SyntheticEmail())).Beliefs;

        var belief = Assert.Single(beliefs);
        Assert.Equal("support_responsiveness", belief.Criterion);
        Assert.Equal(3, belief.Value, precision: 6);
    }

    // ── Rubric banding (BeliefPersistenceStage's exact mechanism, reused directly) ──

    [Theory]
    [InlineData(2.0,  1.00)]  // 0-4h  -> excellent
    [InlineData(8.0,  0.55)]  // 8-24h -> the real escalation-thread evidence's band
    [InlineData(50.0, 0.10)]  // 48h+  -> poor
    public void RawHours_BandToExpectedRubricScore(double rawHours, double expectedScore)
    {
        var score = ObservationModule.ScoreFromRubric("support_response_time", rawHours, Profile);

        Assert.NotNull(score);
        Assert.Equal(expectedScore, score!.Value, precision: 6);
    }

    // ── Index/Posture mechanism proof ───────────────────────────────────────────
    //
    // Deliberately a SYNTHETIC single-belief entity, not a live run of the real KYV pipeline over
    // Scenario 07 — Brookfield/OfficeSpace does not resolve as a persisted vendor via the real
    // pipeline today (the separately-tracked email-identity-resolution gap, KYV_KNOWN_GAPS.md
    // "E-signal Part 5 Step 6"), and processEmail defaults false regardless. This proves the
    // SCORING MECHANISM: once a support_responsiveness belief like the one above is persisted for
    // ANY vendor, Index/Posture now compute from it instead of abstaining.

    [Fact]
    public void Baseline_NoOperationalBeliefs_IndexIsNull()
    {
        var entityId = Guid.NewGuid();
        var empty    = new List<Belief>();
        var scores   = BuildDimensionScores(entityId, empty);

        var index = new IndexModule().Aggregate(
            entityId, scores, empty, previous: null, Profile, DateTimeOffset.UtcNow);

        Assert.Null(index); // every dimension has zero contributing beliefs -- correctly "not assessed"
    }

    [Fact]
    public void SingleSupportResponsivenessBelief_ProducesComputedIndexAndPosture()
    {
        var entityId = Guid.NewGuid();
        var now      = DateTimeOffset.UtcNow;

        // The banded belief this build item's extraction path would now persist for an "over 8
        // hours" observation (Value = the 0.55 rubric score, not the raw 8 -- Belief.Value is
        // documented as "normalised 0-1 rubric score"; banding happens before persistence, see
        // BeliefPersistenceStage.BandIfScored).
        var belief = new Belief(
            Id: Guid.NewGuid(), EntityId: entityId, Dimension: Dimension.Operational,
            Criterion: "support_responsiveness", Value: 0.55, SourceTier: SourceTier.Correspondence,
            Confidence: 0.25, Freshness: 1.0,
            Derivation: "email:0128_escalation_13_0_0.eml \"each take over 8 hours for an initial response\"",
            SourceSignals: [], Version: 1, SupersededBy: null, CreatedAt: now, TraceId: Guid.NewGuid());

        var allBeliefs = new List<Belief> { belief };
        var scores     = BuildDimensionScores(entityId, allBeliefs);

        var index = new IndexModule().Aggregate(
            entityId, scores, allBeliefs, previous: null, Profile, now);

        Assert.NotNull(index); // Operational now has 1 contributing belief -- Aggregate proceeds
        Assert.Equal(Band.AtRisk, index!.Band);
        Assert.Equal(0.25, index.ConfidenceFloor, precision: 6);
        Assert.Equal(0.5125, index.Composite, precision: 4); // (0.55 + 0.5*3) / 4 dimensions, equal weights

        var posture = new PostureModule().Assign(
            index, previousIndex: null, contractRenewalDate: null, Profile, now);

        // Band=AtRisk, pattern=Stable (no previous index), no renewal date -> postures.saas.v1.json's
        // {AtRisk, Stable, null} rule.
        Assert.Equal(Stance.Renegotiate, posture.Stance);
    }

    // ── helpers ──────────────────────────────────────────────────────────────────

    // Mirrors Ii.Spine.IiFacade's own wiring (IiFacade.cs: "anchored.Where(b => b.Dimension ==
    // dirtyDim)") — RubricModule.ScoreDimension does not filter by dimension itself; the caller
    // must pass it only that dimension's beliefs.
    private static IReadOnlyDictionary<Dimension, DimensionScore> BuildDimensionScores(
        Guid entityId, IReadOnlyList<Belief> beliefs)
    {
        var rubric = new RubricModule();
        var dims   = new[] { Dimension.Operational, Dimension.Experiential, Dimension.Financial, Dimension.Strategic };
        return dims.ToDictionary(d => d,
            d => rubric.ScoreDimension(entityId, d, beliefs.Where(b => b.Dimension == d).ToList(), Profile));
    }

    private static ParsedEmail SyntheticEmail() => new(
        FileName: "synthetic.eml",
        From:     new EmailParty("Test Sender", "sender@example.com"),
        To:       [new EmailParty("Test Recipient", "recipient@example.com")],
        Cc:       [],
        Date:     DateTimeOffset.UtcNow,
        Subject:  "Support responsiveness test",
        Body:     "irrelevant -- FakeLlm ignores this and returns canned facts",
        MessageId: "synthetic@example.com",
        InReplyTo: null,
        References: []);

    private sealed class FakeLlm(string responseJson) : IKozmoLlm
    {
        public Task<LlmResult> CompleteJsonAsync(
            string system, string user, int maxTokens = 500, CancellationToken ct = default)
        {
            var el = JsonSerializer.Deserialize<JsonElement>(responseJson);
            return Task.FromResult(new LlmResult(el, 1.0, "fake"));
        }

        public Task<LlmResult> CompleteVisionAsync(
            string system, byte[] imageBytes, int maxTokens = 500, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}
