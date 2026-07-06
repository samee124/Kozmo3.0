using System.Text.Json;
using Ii.CandidateExtraction;
using Kozmo.Contracts;
using Kozmo.Contracts.Config;
using Kozmo.Llm;
using Xunit;

namespace Ii.Tests;

/// <summary>
/// E-signal Part 5 Step 5 — guards added after two rounds of real-email review surfaced them.
/// Sample round (13 emails): (1) a hedged/negotiation-in-progress dollar figure ("roughly $X...
/// approximately $Y annually... this is a starting point... as we finalize") extracted as a
/// settled annual_value belief (0006_pricing.eml); (2) a bare due DATE with no day-count language
/// fabricated into a payment_terms belief (0023_payment_0.eml, "is due on May 11, 2022" ->
/// payment_terms=0). Full-338 round: (3) invoice ISSUANCE cadence mistaken for a payment-DUE
/// period (01_Contract_Kickoff_Mar2021.eml, "submitted within the first 5 business days" ->
/// payment_terms=5 — the day-count guard checks shape, not semantic direction); (4) a
/// multi-invoice YEAR-TOTAL mistaken for one invoice's amount (05_Year_End_Review_Dec2022.eml,
/// "Total invoiced: $153,950... invoices RGL-2022-001 through RGL-2022-004" -> invoice_amount).
/// All four guards are shared with the document path (<see cref="DocumentBeliefExtractor.ParseBeliefs"/>),
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
        // docId must resolve to the "invoice" extraction schema (DocTypeInferrer.InferDocType) —
        // invoice_amount is not in the default/fallback key set, so a non-invoice docId would
        // reject this belief via schema-filtering regardless of the guard, making the test vacuous.
        const string json = """
            {"facts":[{"criterion":"invoice_amount","value":18500,"evidence":"a rough estimate of approximately $18,500 for this milestone","confidence":1.0}],"confidence":1.0,"reasoning":"test"}
            """;
        var extractor = new DocumentBeliefExtractor(new FakeLlm(json), Profile);

        var candidates = (await extractor.ExtractAsync("irrelevant document text", "test_invoice.txt", SourceTier.Verified)).Beliefs;

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

    [Fact]
    public async Task InvoiceIssuanceCadenceEvidence_IsRejected()
    {
        // 01_Contract_Kickoff_Mar2021.eml's real (buggy) evidence — describes when the VENDOR
        // issues invoices, not how long the customer has to pay one. Has the right day-count
        // SHAPE ("5... days"), so only the semantic-direction guard (Fix 3) catches this.
        const string json = """
            {"facts":[{"criterion":"payment_terms","value":5,"evidence":"Our standard invoice cycle is monthly, submitted within the first 5 business days of the following month","confidence":1.0}],"confidence":1.0,"reasoning":"test"}
            """;
        var extractor = new DocumentBeliefExtractor(new FakeLlm(json), Profile);

        var candidates = (await extractor.ExtractAsync("irrelevant document text", "test.txt", SourceTier.Verified)).Beliefs;

        Assert.Empty(candidates);
    }

    [Fact]
    public async Task RetrospectivePaymentTermsEvidence_StillPasses_GuardDoesNotOverDrop()
    {
        // 05_Year_End_Review_Dec2022.eml's real evidence — "settled within Net 45 terms" is a
        // genuine (if retrospective) statement of the payment terms actually used, not an
        // invoice-issuance cadence. Must still extract.
        const string json = """
            {"facts":[{"criterion":"payment_terms","value":45,"evidence":"All invoices settled within Net 45 terms — thank you for the prompt processing","confidence":1.0}],"confidence":1.0,"reasoning":"test"}
            """;
        var extractor = new DocumentBeliefExtractor(new FakeLlm(json), Profile);

        var candidates = (await extractor.ExtractAsync("irrelevant document text", "test.txt", SourceTier.Verified)).Beliefs;

        var candidate = Assert.Single(candidates);
        Assert.Equal("payment_terms", candidate.Criterion);
        Assert.Equal(45, candidate.Value, precision: 6);
    }

    [Fact]
    public async Task MultiInvoiceTotalEvidence_IsRejected()
    {
        // 05_Year_End_Review_Dec2022.eml's real (buggy) evidence — an explicit year-total sum
        // across four invoices, not one invoice's amount.
        const string json = """
            {"facts":[{"criterion":"invoice_amount","value":153950,"evidence":"Total invoiced: $153,950 (per submitted invoices RGL-2022-001 through RGL-2022-004)","confidence":1.0}],"confidence":1.0,"reasoning":"test"}
            """;
        var extractor = new DocumentBeliefExtractor(new FakeLlm(json), Profile);

        var candidates = (await extractor.ExtractAsync("irrelevant document text", "test_invoice.txt", SourceTier.Verified)).Beliefs;

        Assert.Empty(candidates);
    }

    [Theory]
    [InlineData("we've spent $50,000 year-to-date on this engagement")]
    [InlineData("across 6 invoices this quarter, totaling $40,000")]
    public async Task OtherMultiInvoiceAggregatePhrasings_AreAlsoRejected(string evidence)
    {
        var json = $$"""
            {"facts":[{"criterion":"invoice_amount","value":50000,"evidence":"{{evidence}}","confidence":1.0}],"confidence":1.0,"reasoning":"test"}
            """;
        var extractor = new DocumentBeliefExtractor(new FakeLlm(json), Profile);

        var candidates = (await extractor.ExtractAsync("irrelevant document text", "test_invoice.txt", SourceTier.Verified)).Beliefs;

        Assert.Empty(candidates);
    }

    [Fact]
    public async Task SingleInvoiceEvidence_StillPasses_GuardDoesNotOverDrop()
    {
        // A real single-invoice reminder from the Scenario 07 series — must still extract.
        const string json = """
            {"facts":[{"criterion":"invoice_amount","value":10698.15,"evidence":"invoice #88208 for $10,698.15","confidence":1.0}],"confidence":1.0,"reasoning":"test"}
            """;
        var extractor = new DocumentBeliefExtractor(new FakeLlm(json), Profile);

        var candidates = (await extractor.ExtractAsync("irrelevant document text", "test_invoice.txt", SourceTier.Verified)).Beliefs;

        var candidate = Assert.Single(candidates);
        Assert.Equal("invoice_amount", candidate.Criterion);
        Assert.Equal(10698.15, candidate.Value, precision: 6);
    }

    [Fact]
    public async Task ExecutionDateEvidence_IsRejected()
    {
        // 01_MSA_Execution_Confirmation_Apr2022.eml's real (incidental) evidence — an MSA's
        // execution/signing date, not a renewal date. Same third-failure-class as Fixes 3/4a: a
        // real, well-formed date under the wrong semantic role.
        const string json = """
            {"facts":[{"criterion":"renewal_date","value":"2025-04-24","evidence":"has been fully executed as of 24 April 2025","confidence":1.0}],"confidence":1.0,"reasoning":"test"}
            """;
        var extractor = new DocumentBeliefExtractor(new FakeLlm(json), Profile);

        var candidates = (await extractor.ExtractAsync("irrelevant document text", "test.txt", SourceTier.Verified)).Beliefs;

        Assert.Empty(candidates);
    }

    [Theory]
    [InlineData("effective as of March 1, 2024, this Agreement supersedes all prior versions")]
    [InlineData("This Agreement was signed on January 15, 2023")]
    public async Task OtherExecutionDatePhrasings_AreAlsoRejected(string evidence)
    {
        var json = $$"""
            {"facts":[{"criterion":"renewal_date","value":"2024-03-01","evidence":"{{evidence}}","confidence":1.0}],"confidence":1.0,"reasoning":"test"}
            """;
        var extractor = new DocumentBeliefExtractor(new FakeLlm(json), Profile);

        var candidates = (await extractor.ExtractAsync("irrelevant document text", "test.txt", SourceTier.Verified)).Beliefs;

        Assert.Empty(candidates);
    }

    [Fact]
    public async Task GenuineAutoRenewalDateEvidence_StillPasses_GuardDoesNotOverDrop()
    {
        // 08_MSA_Auto_Renewal_Year3_Apr2024.eml's real evidence — a genuine auto-renewal event
        // with a concrete date. Must still extract.
        const string json = """
            {"facts":[{"criterion":"renewal_date","value":"2024-04-22","evidence":"has automatically renewed for its third year effective 22 April 2024","confidence":1.0}],"confidence":1.0,"reasoning":"test"}
            """;
        var extractor = new DocumentBeliefExtractor(new FakeLlm(json), Profile);

        var candidates = (await extractor.ExtractAsync("irrelevant document text", "test.txt", SourceTier.Verified)).Beliefs;

        var candidate = Assert.Single(candidates);
        Assert.Equal("renewal_date", candidate.Criterion);
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
