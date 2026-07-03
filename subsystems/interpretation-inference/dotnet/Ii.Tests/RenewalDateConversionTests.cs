using System.Text.Json;
using Ii.CandidateExtraction;
using Kozmo.Contracts;
using Kozmo.Contracts.Config;
using Kozmo.Llm;
using Xunit;

namespace Ii.Tests;

/// <summary>
/// Commit A — renewal_date date-math fix. The model previously computed the Unix timestamp
/// itself (a known LLM weak spot — see the Salesforce case in KYV_KNOWN_GAPS.md, where the
/// model's timestamp was ~5 years off from its own quoted evidence). The model now emits a
/// plain "YYYY-MM-DD" string; DocumentBeliefExtractor does the epoch conversion deterministically.
///
/// Fake-LLM driven — no cassette needed. Proves the conversion logic in isolation; the real
/// end-to-end result (Salesforce's renewal_date belief matching its evidence) requires the
/// belief-extraction cassette to be re-recorded against the new prompt, which is out of scope
/// for this unit-level check.
/// </summary>
public sealed class RenewalDateConversionTests
{
    private static readonly SaasProfile Profile = TestHelpers.LoadProfile();

    [Fact]
    public async Task PlainDateString_ConvertsToCorrectEpoch_MatchingItsOwnEvidence()
    {
        const string json = """
            {"facts":[{"criterion":"renewal_date","value":"2028-06-30","evidence":"the Term through June 30, 2028","confidence":1.0}],"confidence":1.0,"reasoning":"test"}
            """;
        var extractor = new DocumentBeliefExtractor(new FakeLlm(json), Profile);

        var candidates = await extractor.ExtractAsync("irrelevant document text", "test.txt", SourceTier.Verified);

        var renewal = Assert.Single(candidates);
        Assert.Equal("renewal_date", renewal.Criterion);
        var expected = new DateTimeOffset(2028, 6, 30, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
        Assert.Equal(expected, renewal.Value, precision: 0);
    }

    [Fact]
    public async Task NumericValue_IsNoLongerAccepted_AbstainsInsteadOfTrustingModelArithmetic()
    {
        // Regression guard: a model that reverts to computing its own timestamp (the old,
        // error-prone behavior) must be rejected, not silently accepted as a stale success path.
        const string json = """
            {"facts":[{"criterion":"renewal_date","value":1688083200,"evidence":"the Term through June 30, 2028","confidence":1.0}],"confidence":1.0,"reasoning":"test"}
            """;
        var extractor = new DocumentBeliefExtractor(new FakeLlm(json), Profile);

        var candidates = await extractor.ExtractAsync("irrelevant document text", "test.txt", SourceTier.Verified);

        Assert.Empty(candidates);
    }

    [Fact]
    public async Task UnparseableDateString_Abstains_DoesNotGuess()
    {
        const string json = """
            {"facts":[{"criterion":"renewal_date","value":"not-a-date","evidence":"renews next year","confidence":1.0}],"confidence":1.0,"reasoning":"test"}
            """;
        var extractor = new DocumentBeliefExtractor(new FakeLlm(json), Profile);

        var candidates = await extractor.ExtractAsync("irrelevant document text", "test.txt", SourceTier.Verified);

        Assert.Empty(candidates);
    }

    [Fact]
    public async Task OtherCriteria_StillRequireNumericValue_UnaffectedByDateChange()
    {
        // sla_uptime, csat, payment_terms, annual_value are untouched by this fix.
        const string json = """
            {"facts":[{"criterion":"sla_uptime","value":99.9,"evidence":"99.9% uptime","confidence":1.0}],"confidence":1.0,"reasoning":"test"}
            """;
        var extractor = new DocumentBeliefExtractor(new FakeLlm(json), Profile);

        var candidates = await extractor.ExtractAsync("irrelevant document text", "test.txt", SourceTier.Verified);

        var sla = Assert.Single(candidates);
        Assert.Equal(99.9, sla.Value, precision: 6);
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
