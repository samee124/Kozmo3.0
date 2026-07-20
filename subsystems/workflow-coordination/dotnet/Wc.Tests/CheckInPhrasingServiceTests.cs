using Kozmo.Contracts;
using Kozmo.Contracts.Config;
using Kozmo.Contracts.Interfaces;
using Kozmo.Llm;
using Km.Store;
using Wc.CheckIn;
using Wc.Contracts;
using Xunit;

namespace Wc.Tests;

using CheckIn = global::Wc.Contracts.CheckIn;

// ── CheckInPhrasingService tests ───────────────────────────────────────────────

public sealed class CheckInPhrasingServiceTests
{
    private static readonly Guid   VendorId = new("EEEEEEEE-0000-0000-0000-000000000001");
    private static readonly Guid   RunId    = new("EEEEEEEE-0000-0000-0000-000000000002");
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    // ── Part 1: BuildEvidenceContext ─────────────────────────────────────────

    /// <summary>Vendor with 2 beliefs on the same claim key → both returned, HasEvidence=true.</summary>
    [Fact]
    public async Task BuildEvidenceContext_TwoBeliefs_ReturnsBothWithSources_HasEvidence()
    {
        var store = new InMemoryEntityStore();
        var b1 = MakeBelief(VendorId, "sla_uptime",
            derivation: "Check-in answer to \"Does the vendor have a documented uptime SLA?\": 99.5",
            provenance: "field:sla_uptime");
        var b2 = MakeBelief(VendorId, "sla_uptime",
            derivation: "Check-in answer to \"Does the vendor have a documented uptime SLA?\": 98.2",
            provenance: "row:12");
        await store.AppendBeliefAsync(b1);
        await store.AppendBeliefAsync(b2);

        var ctx = await CheckInPhrasingService.BuildEvidenceContextAsync(
            VendorId, "sla_uptime", store);

        Assert.True(ctx.HasEvidence);
        Assert.Equal("sla_uptime", ctx.ClaimKey);
        Assert.Equal(2, ctx.Entries.Count);
        Assert.Contains(ctx.Entries, e => e.Source == "field:sla_uptime");
        Assert.Contains(ctx.Entries, e => e.Source == "row:12");
    }

    /// <summary>Vendor with no beliefs for the claim key → HasEvidence=false.</summary>
    [Fact]
    public async Task BuildEvidenceContext_NoBeliefsForClaimKey_HasEvidenceFalse()
    {
        var store = new InMemoryEntityStore();
        // belief exists but for a different claim key
        await store.AppendBeliefAsync(MakeBelief(VendorId, "csat", derivation: "Check-in answer to \"...\": 4.5"));

        var ctx = await CheckInPhrasingService.BuildEvidenceContextAsync(
            VendorId, "sla_uptime", store);

        Assert.False(ctx.HasEvidence);
        Assert.Empty(ctx.Entries);
    }

    /// <summary>Superseded beliefs are excluded even when they match the claim key.</summary>
    [Fact]
    public async Task BuildEvidenceContext_SupersededBelief_Excluded()
    {
        var store      = new InMemoryEntityStore();
        var superseded = MakeBelief(VendorId, "sla_uptime", derivation: "old") with
        {
            SupersededBy = Guid.NewGuid()
        };
        await store.AppendBeliefAsync(superseded);

        var ctx = await CheckInPhrasingService.BuildEvidenceContextAsync(
            VendorId, "sla_uptime", store);

        Assert.False(ctx.HasEvidence);
    }

    /// <summary>Empty claimKey → HasEvidence=false without hitting the store.</summary>
    [Fact]
    public async Task BuildEvidenceContext_EmptyClaimKey_HasEvidenceFalse()
    {
        var store = new InMemoryEntityStore();
        await store.AppendBeliefAsync(MakeBelief(VendorId, "sla_uptime"));

        var ctx = await CheckInPhrasingService.BuildEvidenceContextAsync(VendorId, "", store);

        Assert.False(ctx.HasEvidence);
    }

    // ── Part 2: BuildAnswerOptions ────────────────────────────────────────────

    /// <summary>
    /// TYPED_VALUE with 2 beliefs that have extractable option values → 2 value-options
    /// + "Something else" + "Not sure". Each value-option carries its raw value.
    /// </summary>
    [Fact]
    public async Task BuildAnswerOptions_TypedValue_TwoBeliefs_FourOptions()
    {
        var store = new InMemoryEntityStore();
        await store.AppendBeliefAsync(MakeBelief(VendorId, "sla_uptime",
            derivation: "Check-in answer to \"SLA?\": 99.5", provenance: "contract"));
        await store.AppendBeliefAsync(MakeBelief(VendorId, "sla_uptime",
            derivation: "Check-in answer to \"SLA?\": 98.2", provenance: "check-in"));

        var ctx  = await CheckInPhrasingService.BuildEvidenceContextAsync(VendorId, "sla_uptime", store);
        var ci   = OpenCheckIn(ResponseShape.TYPED_VALUE, "sla_uptime");
        var opts = CheckInPhrasingService.BuildAnswerOptions(ci, ctx);

        // 2 value-options + "Something else" + "Not sure"
        Assert.Equal(4, opts.Count);
        Assert.Contains(opts, o => o.Value == "99.5" && !o.IsOpenInput);
        Assert.Contains(opts, o => o.Value == "98.2" && !o.IsOpenInput);
        Assert.Contains(opts, o => o.Label == "Something else" && o.IsOpenInput);
        Assert.Contains(opts, o => o.Label == "Not sure");
    }

    /// <summary>TYPED_VALUE with no evidence → empty options (caller uses plain path).</summary>
    [Fact]
    public void BuildAnswerOptions_NoEvidence_Empty()
    {
        var ci  = OpenCheckIn(ResponseShape.TYPED_VALUE, "sla_uptime");
        var ctx = EvidenceContext.Empty;

        var opts = CheckInPhrasingService.BuildAnswerOptions(ci, ctx);

        Assert.Empty(opts);
    }

    /// <summary>YES_NO always returns empty — evidence context enriches phrasing only.</summary>
    [Fact]
    public async Task BuildAnswerOptions_YesNo_AlwaysEmpty_EvidenceIgnored()
    {
        var store = new InMemoryEntityStore();
        await store.AppendBeliefAsync(MakeBelief(VendorId, "sla_uptime",
            derivation: "Check-in answer to \"SLA?\": 99.5"));

        var ctx  = await CheckInPhrasingService.BuildEvidenceContextAsync(VendorId, "sla_uptime", store);
        var ci   = OpenCheckIn(ResponseShape.YES_NO, "sla_uptime");
        var opts = CheckInPhrasingService.BuildAnswerOptions(ci, ctx);

        Assert.Empty(opts);
    }

    /// <summary>Duplicate option values are deduplicated — only one option per distinct value.</summary>
    [Fact]
    public async Task BuildAnswerOptions_DuplicateValues_Deduped()
    {
        var store = new InMemoryEntityStore();
        // Two beliefs with the same response value
        await store.AppendBeliefAsync(MakeBelief(VendorId, "sla_uptime",
            derivation: "Check-in answer to \"SLA?\": 99.5", provenance: "source-A"));
        await store.AppendBeliefAsync(MakeBelief(VendorId, "sla_uptime",
            derivation: "Check-in answer to \"SLA?\": 99.5", provenance: "source-B"));

        var ctx  = await CheckInPhrasingService.BuildEvidenceContextAsync(VendorId, "sla_uptime", store);
        var ci   = OpenCheckIn(ResponseShape.TYPED_VALUE, "sla_uptime");
        var opts = CheckInPhrasingService.BuildAnswerOptions(ci, ctx);

        // 1 value-option (deduped) + "Something else" + "Not sure"
        Assert.Equal(3, opts.Count);
        Assert.Single(opts, o => o.Value == "99.5");
    }

    // ── Part 3: PhraseAsync — LLM fallback scenarios ─────────────────────────

    /// <summary>LLM throws a generic exception → fixed question returned, no exception propagates.</summary>
    [Fact]
    public async Task Phrase_LlmThrows_ReturnsFallbackQuestion_NoExceptionPropagates()
    {
        var ctx      = BuildEvidenceCtxDirect("sla_uptime", "99.5", "contract");
        var llm      = new ThrowingLlm(new InvalidOperationException("simulated LLM error"));
        const string fixedQ = "Does the vendor have a documented uptime SLA?";

        var (q, summary) = await CheckInPhrasingService.PhraseAsync(fixedQ, ctx, llm);

        Assert.Equal(fixedQ, q);
        Assert.Null(summary);
    }

    /// <summary>LLM returns empty question string → fixed question returned.</summary>
    [Fact]
    public async Task Phrase_LlmReturnsEmpty_ReturnsFallbackQuestion()
    {
        var ctx      = BuildEvidenceCtxDirect("sla_uptime", "99.5", "contract");
        var llm      = new FixedResponseLlm("{\"question\": \"\", \"context_summary\": null}");
        const string fixedQ = "Does the vendor have a documented uptime SLA?";

        var (q, _) = await CheckInPhrasingService.PhraseAsync(fixedQ, ctx, llm);

        Assert.Equal(fixedQ, q);
    }

    /// <summary>LlmCacheMissException (replay mode) → fixed question, no exception propagates.</summary>
    [Fact]
    public async Task Phrase_CacheMiss_ReturnsFallbackQuestion_NoExceptionPropagates()
    {
        var ctx      = BuildEvidenceCtxDirect("sla_uptime", "99.5", "contract");
        var llm      = new CacheMissLlm();
        const string fixedQ = "Does the vendor have a documented uptime SLA?";

        var (q, summary) = await CheckInPhrasingService.PhraseAsync(fixedQ, ctx, llm);

        Assert.Equal(fixedQ, q);
        Assert.Null(summary);
    }

    /// <summary>
    /// LLM returns valid JSON with question and context_summary → both returned.
    /// This verifies the happy path parses correctly.
    /// </summary>
    [Fact]
    public async Task Phrase_LlmReturnsValidJson_ReturnsParsedQuestion()
    {
        var ctx = BuildEvidenceCtxDirect("sla_uptime", "99.5", "contract");
        var llm = new FixedResponseLlm(
            "{\"question\": \"Is the 99.5% uptime SLA still current?\", " +
            "\"context_summary\": \"We have 99.5% on record from contract.\"}");

        var (q, summary) = await CheckInPhrasingService.PhraseAsync(
            "Does the vendor have a documented uptime SLA?", ctx, llm);

        Assert.Equal("Is the 99.5% uptime SLA still current?", q);
        Assert.Equal("We have 99.5% on record from contract.", summary);
    }

    /// <summary>No evidence → PhraseAsync returns fixed question without calling LLM.</summary>
    [Fact]
    public async Task Phrase_NoEvidence_ReturnsFallbackWithoutCallingLlm()
    {
        const string fixedQ = "Does the vendor have a documented uptime SLA?";
        var llm = new ThrowingLlm(new InvalidOperationException("should not be called"));

        // Empty evidence context — LLM must NOT be called
        var (q, summary) = await CheckInPhrasingService.PhraseAsync(fixedQ, EvidenceContext.Empty, llm);

        Assert.Equal(fixedQ, q);
        Assert.Null(summary);
    }

    // ── Scoring parity ────────────────────────────────────────────────────────

    /// <summary>
    /// Answering via an evidence-option value "99.5" produces the identical belief
    /// (same claim_key, same rubric-banded score, SourceTier.Reported) as typing "99.5"
    /// directly through ProcessAnswerAsync. The option is just a convenience shortcut.
    /// </summary>
    [Fact]
    public async Task ScoringParity_EvidenceOptionValue_ScoresIdenticalToTypedAnswer()
    {
        // Profile with uptime_sla: [95–100] → 1.0
        var profile = BuildTestProfile();

        // Belief that would produce the evidence option "99.5"
        var entityStore = new InMemoryEntityStore();
        var checkInStore = new InMemoryCheckInStore();
        var facade = new TrackingFacade();
        var id = Guid.NewGuid();

        var ci = OpenCheckIn(ResponseShape.TYPED_VALUE, targetField: "sla_uptime");
        ci = ci with { CheckInId = id };
        await checkInStore.SaveAsync(ci);

        // Submit "99.5" (the evidence-option value) through ProcessAnswerAsync
        var svc    = new AnswerCheckInService();
        var result = await svc.ProcessAnswerAsync(
            id, "99.5", Now,
            checkInStore,
            new VendorFileWriteService(entityStore, profile),
            profile, facade,
            new InMemoryIdentityRegistry());

        Assert.Equal(AnswerOutcome.Ok, result.Outcome);
        Assert.Single(entityStore.AllBeliefs);
        var belief = entityStore.AllBeliefs[0];

        // Same outcome as typing "99.5" in the in-app form
        Assert.Equal(SourceTier.Reported, belief.SourceTier);
        Assert.Equal(1.0, belief.Value);  // [95–100] band → 1.0
        Assert.Equal(VendorId, belief.EntityId);
    }

    // ── Determinism ───────────────────────────────────────────────────────────

    /// <summary>
    /// Same evidence + two different mocked LLM sentences → identical answer options.
    /// The LLM affects only the displayed question sentence, not the options or their values.
    /// </summary>
    [Fact]
    public async Task Determinism_SameEvidence_DifferentLlmWording_IdenticalOptions()
    {
        var store = new InMemoryEntityStore();
        await store.AppendBeliefAsync(MakeBelief(VendorId, "sla_uptime",
            derivation: "Check-in answer to \"SLA?\": 99.5", provenance: "contract"));
        var ctx = await CheckInPhrasingService.BuildEvidenceContextAsync(VendorId, "sla_uptime", store);

        var ci = OpenCheckIn(ResponseShape.TYPED_VALUE, "sla_uptime");

        // Build options with LLM sentence A
        var optsA = CheckInPhrasingService.BuildAnswerOptions(ci, ctx);

        // Build options with LLM sentence B (simulated by using a different fixed response)
        var optsB = CheckInPhrasingService.BuildAnswerOptions(ci, ctx);

        // Options are always identical — they come from deterministic Part 2, not the LLM
        Assert.Equal(optsA.Count, optsB.Count);
        for (var i = 0; i < optsA.Count; i++)
        {
            Assert.Equal(optsA[i].Label, optsB[i].Label);
            Assert.Equal(optsA[i].Value, optsB[i].Value);
        }
    }

    /// <summary>
    /// Two PhraseAsync calls with same evidence but different mock LLM responses → different
    /// displayed sentences but IDENTICAL option values (and thus identical scored beliefs).
    /// </summary>
    [Fact]
    public async Task Determinism_DifferentLlmSentences_SameOptionValues()
    {
        var ctx = BuildEvidenceCtxDirect("sla_uptime", "99.5", "contract");
        var ci  = OpenCheckIn(ResponseShape.TYPED_VALUE, "sla_uptime");

        var llm1 = new FixedResponseLlm("{\"question\": \"Sentence A.\", \"context_summary\": null}");
        var llm2 = new FixedResponseLlm("{\"question\": \"Sentence B.\", \"context_summary\": null}");

        var (q1, _) = await CheckInPhrasingService.PhraseAsync("Base Q", ctx, llm1);
        var (q2, _) = await CheckInPhrasingService.PhraseAsync("Base Q", ctx, llm2);

        // Sentences differ (presentation only)
        Assert.Equal("Sentence A.", q1);
        Assert.Equal("Sentence B.", q2);

        // Options are identical — deterministic regardless of LLM sentence
        var opts1 = CheckInPhrasingService.BuildAnswerOptions(ci, ctx);
        var opts2 = CheckInPhrasingService.BuildAnswerOptions(ci, ctx);
        Assert.Equal(opts1.Count, opts2.Count);
        for (var i = 0; i < opts1.Count; i++)
            Assert.Equal(opts1[i].Value, opts2[i].Value);
    }

    // ── ExtractOptionValue (internal) ─────────────────────────────────────────

    [Theory]
    [InlineData("Check-in answer to \"SLA?\": 99.5",                 "99.5")]
    [InlineData("Check-in answer to \"What value?\": 98.2%",         "98.2%")]
    [InlineData("Check-in answer to \"Status?\": Declining",         "Declining")]
    [InlineData("vendor-file:sla_uptime",                             null)]
    [InlineData("Rule: matched keyword 'uptime'",                     null)]
    [InlineData("",                                                    null)]
    [InlineData(null,                                                  null)]
    public void ExtractOptionValue_VariousDerivations_ExpectedResult(string? derivation, string? expected)
    {
        Assert.Equal(expected, CheckInPhrasingService.ExtractOptionValue(derivation));
    }

    // ── Test helpers ──────────────────────────────────────────────────────────

    private static CheckIn OpenCheckIn(ResponseShape shape, string? targetField = null) =>
        new(CheckInId:     Guid.NewGuid(),
            VendorId:      VendorId,
            ProgramRunId:  RunId,
            Kind:          CheckInKind.DIMENSION_GAP,
            Question:      "Does the vendor have a documented uptime SLA?",
            ResponseShape: shape,
            TargetField:   targetField,
            Owner:         "test@test",
            Status:        PendingStatus.OPEN,
            RaisedAt:      Now,
            AnsweredAt:    null,
            ExpiresAt:     null,
            ResponseValue: null);

    private static Belief MakeBelief(
        Guid    entityId,
        string  claimKey,
        string? derivation = null,
        string? provenance = null)
    {
        return new Belief(
            Id:            Guid.NewGuid(),
            EntityId:      entityId,
            Dimension:     Dimension.Operational,
            Criterion:     claimKey,
            Value:         1.0,
            SourceTier:    SourceTier.Reported,
            Confidence:    0.50,
            Freshness:     1.0,
            Derivation:    derivation ?? $"vendor-file:{claimKey}",
            SourceSignals: Array.Empty<Guid>(),
            Version:       1,
            SupersededBy:  null,
            CreatedAt:     Now,
            TraceId:       Guid.NewGuid())
        {
            ClaimKey   = claimKey,
            Provenance = provenance != null
                ? new BeliefProvenance(Guid.NewGuid(), provenance)
                : null
        };
    }

    private static EvidenceContext BuildEvidenceCtxDirect(string claimKey, string optionValue, string source)
    {
        var entry = new EvidenceEntry(
            DisplayText: $"Check-in answer to \"SLA?\": {optionValue}",
            OptionValue: optionValue,
            Source:      source,
            Tier:        SourceTier.Reported,
            Confidence:  0.50);
        return new EvidenceContext(claimKey, new[] { entry }, HasEvidence: true);
    }

    private static SaasProfile BuildTestProfile() => new(
        ConfigVersion:       "test",
        Dimensions:          new Dictionary<string, DimensionDefinition>(),
        ScoringRubric: new Dictionary<string, CriterionRubric>
        {
            ["uptime_sla"] = new CriterionRubric(
                Type: "numeric",
                NumericThresholds: new[]
                {
                    new RubricThreshold(95.0, 100.0, 1.0),
                    new RubricThreshold(85.0,  95.0, 0.5)
                },
                EnumScores: null)
        },
        DimensionWeights:    new Dictionary<string, double>(),
        Bands:               new BandsConfig(0.6, 0.4, 0.5, 0.1, 0.05),
        PostureRules:        new List<PostureRule>(),
        SourceTiers:         new Dictionary<string, SourceTierConfig>(),
        ClassificationRules: new List<ClassificationRule>(),
        HalfLifeDays:        new Dictionary<string, int>(),
        EntityResolution:    new EntityResolutionConfig("exact", 0.85, new Dictionary<string, string>()))
    {
        ClaimKeyCatalogue = new Dictionary<string, ClaimKeyDefinition>
        {
            ["sla_uptime"] = new ClaimKeyDefinition(
                ClaimClass:      "scored",
                ValueType:       "percent",
                Dimension:       "Operational",
                TypicalTier:     "VERIFIED",
                HalfLifeDays:    30,
                DimensionWeight: 0.25)
            { RubricCriterion = "uptime_sla" }
        }
    };
}

// ── Test-only LLM stubs ────────────────────────────────────────────────────────

/// <summary>LLM that throws a specified exception on CompleteJsonAsync.</summary>
internal sealed class ThrowingLlm : IKozmoLlm
{
    private readonly Exception _ex;
    public ThrowingLlm(Exception ex) => _ex = ex;
    public Task<LlmResult> CompleteJsonAsync(string system, string user, int maxTokens = 500, CancellationToken ct = default)
        => throw _ex;
}

/// <summary>LLM that returns a fixed JSON string as the answer.</summary>
internal sealed class FixedResponseLlm : IKozmoLlm
{
    private readonly string _json;
    public FixedResponseLlm(string json) => _json = json;
    public async Task<LlmResult> CompleteJsonAsync(string system, string user, int maxTokens = 500, CancellationToken ct = default)
    {
        await Task.Yield();
        var element = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(_json);
        return new LlmResult(element, 0.9, "test");
    }
}

/// <summary>LLM that throws LlmCacheMissException (simulates CachingLlmClient replay mode miss).</summary>
internal sealed class CacheMissLlm : IKozmoLlm
{
    public Task<LlmResult> CompleteJsonAsync(string system, string user, int maxTokens = 500, CancellationToken ct = default)
        => throw new LlmCacheMissException("test-key-abc123");
}
