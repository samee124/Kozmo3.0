using System.Text.Json;
using Ii.CandidateExtraction;
using Kozmo.Contracts;
using Kozmo.Contracts.Config;
using Kozmo.Llm;
using Xunit;

namespace Ii.Tests;

/// <summary>
/// Commit C — csat prompt precision, plus a narrow deterministic backstop. BeliefExtractionPrompt.
/// System used to say "customer satisfaction / quality score", making the two interchangeable —
/// which is exactly why a real IIVS document's "study quality scores averaged 4.6 out of 5.0"
/// (a QA metric on lab study deliverables, not customer sentiment) got extracted as csat (see
/// KYV_KNOWN_GAPS.md). The prompt rule was tightened to require explicit customer-sentiment/
/// survey framing and now names study/product quality scores as a negative example.
///
/// A pure prompt edit can't be proven with a fake LLM (a fake can be made to return anything,
/// which only tests the extractor's code, not whether the real model still misclassifies this
/// evidence) — so, matching Commit B's pattern, DocumentBeliefExtractor also enforces the two
/// specific negative-example phrases deterministically. This is deliberately narrow (not a
/// general "must mention customer" filter, which would risk rejecting legitimately-phrased CSAT
/// evidence) — it backstops exactly the confusion already proven to happen, and makes that case
/// testable here without a live LLM call.
///
/// Fake-LLM driven — no cassette needed.
/// </summary>
public sealed class CsatQualityScoreGuardTests
{
    private static readonly SaasProfile Profile = TestHelpers.LoadProfile();

    [Fact]
    public async Task StudyQualityScoreEvidence_IsRejected_NoCsatBelief()
    {
        const string json = """
            {"facts":[{"criterion":"csat","value":4.6,"evidence":"Study quality scores averaged 4.6 out of 5.0 based on sponsor feedback surveys.","confidence":1.0}],"confidence":1.0,"reasoning":"test"}
            """;
        var extractor = new DocumentBeliefExtractor(new FakeLlm(json), Profile);

        var candidates = await extractor.ExtractAsync("irrelevant document text", "test.txt", SourceTier.Verified);

        Assert.Empty(candidates);
    }

    [Fact]
    public async Task RealCustomerSatisfactionEvidence_StillPasses()
    {
        const string json = """
            {"facts":[{"criterion":"csat","value":4.6,"evidence":"Customer satisfaction survey: 4.6 out of 5.0","confidence":1.0}],"confidence":1.0,"reasoning":"test"}
            """;
        var extractor = new DocumentBeliefExtractor(new FakeLlm(json), Profile);

        var candidates = await extractor.ExtractAsync("irrelevant document text", "test.txt", SourceTier.Verified);

        var candidate = Assert.Single(candidates);
        Assert.Equal("csat", candidate.Criterion);
        Assert.Equal(4.6, candidate.Value, precision: 6);
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
