using System.Text.Json;
using Ii.CandidateExtraction;
using Kozmo.Contracts;
using Kozmo.Contracts.Config;
using Kozmo.Llm;
using Xunit;

namespace Ii.Tests;

/// <summary>
/// E-signal Part 5 Step 5 — two guards added after the real 13-email sample review surfaced them:
/// (1) a hedged/negotiation-in-progress dollar figure ("roughly $X... approximately $Y annually...
/// this is a starting point... as we finalize") extracted as a settled annual_value belief
/// (0006_pricing.eml); (2) a bare due DATE with no day-count language fabricated into a
/// payment_terms belief (0023_payment_0.eml, "is due on May 11, 2022" -> payment_terms=0). Both
/// guards are shared with the document path (<see cref="DocumentBeliefExtractor.ParseBeliefs"/>),
/// so these tests exercise them the same way <c>PaymentTermsTerminationGuardTests</c> does — via
/// <see cref="DocumentBeliefExtractor.ExtractAsync"/> with a fake LLM, no cassette needed.
/// </summary>
public sealed class HedgedProposalAndDayCountGuardTests
{
    private static readonly SaasProfile Profile = TestHelpers.LoadProfile();

    [Theory]
    [InlineData("roughly $14.50/seat/month, approximately $147,900 annually")]
    [InlineData("This is a starting point and I'm sure we can find efficiencies as we finalize seat counts")]
    [InlineData("an estimated $250,000 for the year")]
    [InlineData("ballpark figure of $90,000")]
    [InlineData("somewhere in the range of $80,000 to $100,000")]
    public async Task HedgedAnnualValueEvidence_IsRejected(string evidence)
    {
        var json = $$"""
            {"facts":[{"criterion":"annual_value","value":147900,"evidence":"{{evidence}}","confidence":1.0}],"confidence":1.0,"reasoning":"test"}
            """;
        var extractor = new DocumentBeliefExtractor(new FakeLlm(json), Profile);

        var candidates = (await extractor.ExtractAsync("irrelevant document text", "test.txt", SourceTier.Verified)).Beliefs;

        Assert.Empty(candidates);
    }

    [Fact]
    public async Task HedgedInvoiceAmountEvidence_IsAlsoRejected()
    {
        const string json = """
            {"facts":[{"criterion":"invoice_amount","value":18500,"evidence":"a rough estimate of approximately $18,500 for this milestone","confidence":1.0}],"confidence":1.0,"reasoning":"test"}
            """;
        var extractor = new DocumentBeliefExtractor(new FakeLlm(json), Profile);

        var candidates = (await extractor.ExtractAsync("irrelevant document text", "test.txt", SourceTier.Verified)).Beliefs;

        Assert.Empty(candidates);
    }

    [Fact]
    public async Task SettledAnnualValueEvidence_StillPasses_GuardDoesNotOverDrop()
    {
        // 05_SOW02_Initiation_PO_Confirmation_Mar2023.eml's real evidence — a confirmed PO total,
        // no hedging language — must still extract.
        const string json = """
            {"facts":[{"criterion":"annual_value","value":92000,"evidence":"Total Value: $92,000","confidence":1.0}],"confidence":1.0,"reasoning":"test"}
            """;
        var extractor = new DocumentBeliefExtractor(new FakeLlm(json), Profile);

        var candidates = (await extractor.ExtractAsync("irrelevant document text", "test.txt", SourceTier.Verified)).Beliefs;

        var candidate = Assert.Single(candidates);
        Assert.Equal("annual_value", candidate.Criterion);
        Assert.Equal(92000, candidate.Value, precision: 6);
    }

    [Fact]
    public async Task BareDueDateEvidence_NoDayCount_IsRejected()
    {
        // 0023_payment_0.eml's real (buggy) evidence — a due date with no day-count language.
        const string json = """
            {"facts":[{"criterion":"payment_terms","value":0,"evidence":"invoice #88208 for $10,698.15 is due on May 11, 2022","confidence":1.0}],"confidence":1.0,"reasoning":"test"}
            """;
        var extractor = new DocumentBeliefExtractor(new FakeLlm(json), Profile);

        var candidates = (await extractor.ExtractAsync("irrelevant document text", "test.txt", SourceTier.Verified)).Beliefs;

        Assert.Empty(candidates);
    }

    [Fact]
    public async Task NetNWithoutTheWordDays_StillPasses_GuardDoesNotOverDrop()
    {
        // 03_Invoice_Query_Jul2022.eml's real evidence — "Net 45" with no "days" word at all.
        const string json = """
            {"facts":[{"criterion":"payment_terms","value":45,"evidence":"AP should be able to process within the standard Net 45 window.","confidence":1.0}],"confidence":1.0,"reasoning":"test"}
            """;
        var extractor = new DocumentBeliefExtractor(new FakeLlm(json), Profile);

        var candidates = (await extractor.ExtractAsync("irrelevant document text", "test.txt", SourceTier.Verified)).Beliefs;

        var candidate = Assert.Single(candidates);
        Assert.Equal("payment_terms", candidate.Criterion);
        Assert.Equal(45, candidate.Value, precision: 6);
    }

    [Fact]
    public async Task DayCountWithIntervalPunctuation_StillPasses_GuardDoesNotOverDrop()
    {
        // The existing real document phrasing PaymentTermsTerminationGuardTests already pins —
        // confirms the day-count guard doesn't require the digit and "days" to be adjacent.
        const string json = """
            {"facts":[{"criterion":"payment_terms","value":30,"evidence":"All invoices are due and payable within thirty (30) days of the invoice date","confidence":1.0}],"confidence":1.0,"reasoning":"test"}
            """;
        var extractor = new DocumentBeliefExtractor(new FakeLlm(json), Profile);

        var candidates = (await extractor.ExtractAsync("irrelevant document text", "test.txt", SourceTier.Verified)).Beliefs;

        var candidate = Assert.Single(candidates);
        Assert.Equal("payment_terms", candidate.Criterion);
        Assert.Equal(30, candidate.Value, precision: 6);
    }

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
