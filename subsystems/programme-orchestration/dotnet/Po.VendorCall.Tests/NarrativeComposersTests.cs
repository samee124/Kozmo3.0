using System.Text.Json;
using Kozmo.Llm;
using Po.VendorCall;
using Xunit;

namespace Po.VendorCall.Tests;

/// <summary>
/// Tests for all six narrative composers (Q1–Q5 + Overview).
/// Three paths per composer: deterministic (null LLM), LLM-enhanced, LLM-fallback.
/// Uses Northstar scenario fixtures via FactAssemblersTestFixtures.
/// </summary>
public sealed class NarrativeComposersTests
{
    // ── Shared fixture helpers ────────────────────────────────────────────────

    private static Q1FactPacket MakeQ1() =>
        new Q1FactAssembler().Assemble(
            FactAssemblersTestFixtures.NorthstarBundle(),
            FactAssemblersTestFixtures.NorthstarBeliefs(),
            previousCheckpoint: null,
            vendorName:         "Northstar Software",
            eventTypeCode:      "vendor_review",
            today:              FactAssemblersTestFixtures.Now);

    private static Q2FactPacket MakeQ2() =>
        new Q2FactAssembler().Assemble(
            FactAssemblersTestFixtures.NorthstarBundle(),
            FactAssemblersTestFixtures.NorthstarBeliefs(),
            previousCheckpoint: null,
            now:                FactAssemblersTestFixtures.Now);

    private static Q3FactPacket MakeQ3() =>
        new Q3FactAssembler().Assemble(
            FactAssemblersTestFixtures.NorthstarBundle(),
            FactAssemblersTestFixtures.NorthstarBeliefs(),
            previousCheckpoint: null,
            now:                FactAssemblersTestFixtures.Now);

    private static Q4FactPacket MakeQ4() =>
        new Q4FactAssembler().Assemble(
            FactAssemblersTestFixtures.NorthstarBundle(),
            FactAssemblersTestFixtures.NorthstarBeliefs(),
            now:                FactAssemblersTestFixtures.Now);

    private static Q5FactPacket MakeQ5()
    {
        var q4 = MakeQ4();
        return new Q5FactAssembler().Assemble(
            FactAssemblersTestFixtures.NorthstarBundle(),
            q4,
            ownerUpn:    "rishi@econtracts.onmicrosoft.com",
            beliefs:     FactAssemblersTestFixtures.NorthstarBeliefs(),
            now:         FactAssemblersTestFixtures.Now);
    }

    // ── Q1 composer ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Q1_NullLlm_LlmEnhancedFalse()
    {
        var answer = await new Q1NarrativeComposer(llm: null).ComposeAsync(MakeQ1());
        Assert.False(answer.LlmEnhanced);
    }

    [Fact]
    public async Task Q1_NullLlm_TextContainsVendorName()
    {
        var answer = await new Q1NarrativeComposer(llm: null).ComposeAsync(MakeQ1());
        Assert.Contains("Northstar Software", answer.Text);
    }

    [Fact]
    public async Task Q1_NullLlm_TextContainsMeetingType()
    {
        var answer = await new Q1NarrativeComposer(llm: null).ComposeAsync(MakeQ1());
        Assert.Contains("quarterly vendor review", answer.Text);
    }

    [Fact]
    public async Task Q1_SucceedingLlm_LlmEnhancedTrue()
    {
        var answer = await new Q1NarrativeComposer(new NarrativeSucceedingLlm()).ComposeAsync(MakeQ1());
        Assert.True(answer.LlmEnhanced);
    }

    [Fact]
    public async Task Q1_ThrowingLlm_FallsBackToDeterministic()
    {
        var answer = await new Q1NarrativeComposer(new NarrativeThrowingLlm()).ComposeAsync(MakeQ1());
        Assert.False(answer.LlmEnhanced);
        Assert.Contains("Northstar Software", answer.Text);
    }

    [Fact]
    public async Task Q1_GroundingFailingLlm_FallsBackToDeterministic()
    {
        var answer = await new Q1NarrativeComposer(new NarrativeGroundingFailingLlm())
            .ComposeAsync(MakeQ1());
        Assert.False(answer.LlmEnhanced);
    }

    // ── Q2 composer ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Q2_NullLlm_LlmEnhancedFalse()
    {
        var answer = await new Q2NarrativeComposer(llm: null).ComposeAsync(MakeQ2());
        Assert.False(answer.LlmEnhanced);
    }

    [Fact]
    public async Task Q2_NullLlm_TextContractsPresent()
    {
        var answer = await new Q2NarrativeComposer(llm: null).ComposeAsync(MakeQ2());
        Assert.Contains("Active contract", answer.Text);
    }

    [Fact]
    public async Task Q2_SucceedingLlm_LlmEnhancedTrue()
    {
        var answer = await new Q2NarrativeComposer(new NarrativeSucceedingLlm()).ComposeAsync(MakeQ2());
        Assert.True(answer.LlmEnhanced);
    }

    [Fact]
    public async Task Q2_ThrowingLlm_FallsBackToDeterministic()
    {
        var answer = await new Q2NarrativeComposer(new NarrativeThrowingLlm()).ComposeAsync(MakeQ2());
        Assert.False(answer.LlmEnhanced);
    }

    // ── Q3 composer ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Q3_NullLlm_LlmEnhancedFalse()
    {
        var answer = await new Q3NarrativeComposer(llm: null).ComposeAsync(MakeQ3());
        Assert.False(answer.LlmEnhanced);
    }

    [Fact]
    public async Task Q3_NullLlm_TextContainsHelping()
    {
        var answer = await new Q3NarrativeComposer(llm: null).ComposeAsync(MakeQ3());
        Assert.Contains("Helping:", answer.Text);
    }

    [Fact]
    public async Task Q3_NullLlm_TextContainsPreventing()
    {
        var answer = await new Q3NarrativeComposer(llm: null).ComposeAsync(MakeQ3());
        Assert.Contains("Preventing:", answer.Text);
    }

    [Fact]
    public async Task Q3_SucceedingLlm_LlmEnhancedTrue()
    {
        var answer = await new Q3NarrativeComposer(new NarrativeSucceedingLlm()).ComposeAsync(MakeQ3());
        Assert.True(answer.LlmEnhanced);
    }

    [Fact]
    public async Task Q3_ThrowingLlm_FallsBackToDeterministic()
    {
        var answer = await new Q3NarrativeComposer(new NarrativeThrowingLlm()).ComposeAsync(MakeQ3());
        Assert.False(answer.LlmEnhanced);
    }

    // ── Q4 composer ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Q4_NullLlm_LlmEnhancedFalse()
    {
        var answer = await new Q4NarrativeComposer(llm: null).ComposeAsync(MakeQ4());
        Assert.False(answer.LlmEnhanced);
    }

    [Fact]
    public async Task Q4_NullLlm_TextContainsTopPriorities()
    {
        var answer = await new Q4NarrativeComposer(llm: null).ComposeAsync(MakeQ4());
        Assert.Contains("Top priorities", answer.Text);
    }

    [Fact]
    public async Task Q4_SucceedingLlm_LlmEnhancedTrue()
    {
        var answer = await new Q4NarrativeComposer(new NarrativeSucceedingLlm()).ComposeAsync(MakeQ4());
        Assert.True(answer.LlmEnhanced);
    }

    [Fact]
    public async Task Q4_ThrowingLlm_FallsBackToDeterministic()
    {
        var answer = await new Q4NarrativeComposer(new NarrativeThrowingLlm()).ComposeAsync(MakeQ4());
        Assert.False(answer.LlmEnhanced);
    }

    // ── Q5 composer ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Q5_NullLlm_LlmEnhancedFalse()
    {
        var answer = await new Q5NarrativeComposer(llm: null).ComposeAsync(MakeQ5());
        Assert.False(answer.LlmEnhanced);
    }

    [Fact]
    public async Task Q5_NullLlm_TextContainsRecommendedActions()
    {
        var answer = await new Q5NarrativeComposer(llm: null).ComposeAsync(MakeQ5());
        Assert.Contains("Recommended actions", answer.Text);
    }

    [Fact]
    public async Task Q5_SucceedingLlm_LlmEnhancedTrue()
    {
        var answer = await new Q5NarrativeComposer(new NarrativeSucceedingLlm()).ComposeAsync(MakeQ5());
        Assert.True(answer.LlmEnhanced);
    }

    [Fact]
    public async Task Q5_ThrowingLlm_FallsBackToDeterministic()
    {
        var answer = await new Q5NarrativeComposer(new NarrativeThrowingLlm()).ComposeAsync(MakeQ5());
        Assert.False(answer.LlmEnhanced);
    }

    // ── Overview composer ─────────────────────────────────────────────────────

    [Fact]
    public async Task Overview_NullLlm_LlmEnhancedFalse()
    {
        var answer = await new OverviewNarrativeComposer(llm: null)
            .ComposeAsync(ReviewStatus.Amber, ReviewMovement.Stable, ReviewConfidence.Medium,
                          MakeQ1(), MakeQ2());
        Assert.False(answer.LlmEnhanced);
    }

    [Fact]
    public async Task Overview_NullLlm_TextContainsStatusAndMovement()
    {
        var answer = await new OverviewNarrativeComposer(llm: null)
            .ComposeAsync(ReviewStatus.Amber, ReviewMovement.Stable, ReviewConfidence.Medium,
                          MakeQ1(), MakeQ2());
        Assert.Contains("Amber",  answer.Text);
        Assert.Contains("Stable", answer.Text);
    }

    [Fact]
    public async Task Overview_SucceedingLlm_LlmEnhancedTrue()
    {
        var answer = await new OverviewNarrativeComposer(new NarrativeSucceedingLlm())
            .ComposeAsync(ReviewStatus.Green, ReviewMovement.Improving, ReviewConfidence.High,
                          MakeQ1(), MakeQ2());
        Assert.True(answer.LlmEnhanced);
    }

    [Fact]
    public async Task Overview_ThrowingLlm_FallsBackToDeterministic()
    {
        var answer = await new OverviewNarrativeComposer(new NarrativeThrowingLlm())
            .ComposeAsync(ReviewStatus.Red, ReviewMovement.Weakening, ReviewConfidence.Low,
                          MakeQ1(), MakeQ2());
        Assert.False(answer.LlmEnhanced);
        Assert.Contains("Red", answer.Text);
    }

    [Fact]
    public async Task Overview_NullLlm_TextContainsVendorName()
    {
        var answer = await new OverviewNarrativeComposer(llm: null)
            .ComposeAsync(ReviewStatus.Amber, ReviewMovement.Stable, ReviewConfidence.Medium,
                          MakeQ1(), MakeQ2());
        Assert.Contains("Northstar Software", answer.Text);
    }
}

// ── Test doubles ──────────────────────────────────────────────────────────────

/// <summary>Returns a plain narrative answer with no dates or large numbers (always passes grounding).</summary>
internal sealed class NarrativeSucceedingLlm : IKozmoLlm
{
    public Task<LlmResult> CompleteJsonAsync(
        string system, string user, int maxTokens = 500, CancellationToken ct = default)
    {
        var json   = "{\"text\": \"LLM-enhanced narrative answer.\"}";
        var result = new LlmResult(JsonDocument.Parse(json).RootElement, 0.95, "stub");
        return Task.FromResult(result);
    }
}

/// <summary>Throws an exception to simulate LLM unavailability.</summary>
internal sealed class NarrativeThrowingLlm : IKozmoLlm
{
    public Task<LlmResult> CompleteJsonAsync(
        string system, string user, int maxTokens = 500, CancellationToken ct = default)
        => throw new InvalidOperationException("LLM unavailable");
}

/// <summary>Returns a hallucinated date (2099-01-01) that will fail the grounding check.</summary>
internal sealed class NarrativeGroundingFailingLlm : IKozmoLlm
{
    public Task<LlmResult> CompleteJsonAsync(
        string system, string user, int maxTokens = 500, CancellationToken ct = default)
    {
        var json   = "{\"text\": \"The renewal deadline is 2099-01-01.\"}";
        var result = new LlmResult(JsonDocument.Parse(json).RootElement, 0.95, "stub");
        return Task.FromResult(result);
    }
}
