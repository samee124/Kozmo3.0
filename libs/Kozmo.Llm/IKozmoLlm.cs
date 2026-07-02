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

    /// <summary>
    /// Vision / OCR path — sends a single raster image to a vision-capable model and returns
    /// the model's text response wrapped in an <see cref="LlmResult"/>. Answer is a
    /// <see cref="System.Text.Json.JsonElement"/> of kind String containing the extracted text.
    /// <para>
    /// <b>Soft-edge:</b> the default implementation throws <see cref="NotSupportedException"/>.
    /// Only <see cref="CachingLlmClient"/> (backed by a vision-capable inner) and
    /// <see cref="OpenAiLlmClient"/> fully implement this path. Callers that invoke OCR must
    /// catch <see cref="NotSupportedException"/> and treat the document as unreadable.
    /// </para>
    /// <para>
    /// The cassette key is <c>SHA256("vision|model|temp|system|SHA256(imageBytes)|maxTokens")</c>
    /// so replay is deterministic: same image bytes always hit the same cassette entry and
    /// cannot collide with text-prompt keys.
    /// </para>
    /// </summary>
    Task<LlmResult> CompleteVisionAsync(
        string            system,
        byte[]            imageBytes,
        int               maxTokens = 500,
        CancellationToken ct        = default)
        => throw new NotSupportedException(
               $"{GetType().Name} does not implement vision. " +
               "Use a CachingLlmClient backed by OpenAiLlmClient for OCR calls.");
}

/// <summary>Raw output from one LLM call. Answer is the parsed JSON object (caller-typed).</summary>
public readonly record struct LlmResult(
    object? Answer,
    double  Confidence,
    string  ReasoningSummary);
