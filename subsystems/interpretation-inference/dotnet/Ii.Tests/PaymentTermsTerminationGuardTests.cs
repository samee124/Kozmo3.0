using System.Text.Json;
using Ii.CandidateExtraction;
using Kozmo.Contracts;
using Kozmo.Contracts.Config;
using Kozmo.Llm;
using Xunit;

namespace Ii.Tests;

/// <summary>
/// Commit B — payment_terms termination-notice guard. BeliefExtractionPrompt.System already
/// explicitly bans extracting payment_terms from termination/cancellation/insurance notice
/// periods, and the model did it anyway on a real document (a Salesforce amendment's "terminate
/// this Agreement... upon 30 days prior written notice" — see KYV_KNOWN_GAPS.md). Re-stating an
/// already-explicit rule wasn't going to fix it, so DocumentBeliefExtractor now enforces it
/// deterministically in code, regardless of what the model returns.
///
/// Fake-LLM driven — no cassette needed, and none is touched: this is a post-extraction filter,
/// not a prompt change, so the LLM call and cache key are unaffected.
/// </summary>
public sealed class PaymentTermsTerminationGuardTests
{
    private static readonly SaasProfile Profile = TestHelpers.LoadProfile();

    [Fact]
    public async Task TerminationNoticeEvidence_IsRejected_NoPaymentTermsBelief()
    {
        const string json = """
            {"facts":[{"criterion":"payment_terms","value":30,"evidence":"Customer may terminate this Agreement upon 30 days prior written notice","confidence":1.0}],"confidence":1.0,"reasoning":"test"}
            """;
        var extractor = new DocumentBeliefExtractor(new FakeLlm(json), Profile);

        var candidates = (await extractor.ExtractAsync("irrelevant document text", "test.txt", SourceTier.Verified)).Beliefs;

        Assert.Empty(candidates);
    }

    [Fact]
    public async Task RealInvoicePaymentTermsEvidence_StillPasses_GuardDoesNotOverDrop()
    {
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
