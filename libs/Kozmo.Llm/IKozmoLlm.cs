// Contract frozen in Step 1.0.
// Implementations (Dev B, Phase 1):
//   CachingLlmClient — serves from frozen cache; cache miss is a hard error in demo/test runtime
//   LlmClient        — real Anthropic; reachable ONLY from seed-prep / smoke entrypoints

namespace Kozmo.Llm;

/// <summary>
/// LLM abstraction. In the demo runtime path this is always resolved to CachingLlmClient.
/// A cache miss in CachingLlmClient fails the run — no live network call in the demo path.
/// </summary>
public interface IKozmoLlm
{
    Task<LlmResult> CompleteJsonAsync(
        string system,
        string user,
        int    maxTokens = 500,
        CancellationToken ct = default);
}

/// <summary>Raw output from one LLM call. Answer is the parsed JSON object (caller-typed).</summary>
public readonly record struct LlmResult(
    object? Answer,
    double  Confidence,
    string  ReasoningSummary);
