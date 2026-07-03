namespace Kozmo.Llm.Tests;

/// <summary>Deterministic stub for testing CachingLlmClient in record mode.</summary>
internal sealed class FakeLlmClient : IKozmoLlm
{
    private readonly LlmResult  _result;
    private readonly LlmResult? _visionResult;

    public FakeLlmClient(LlmResult result, LlmResult? visionResult = null)
    {
        _result       = result;
        _visionResult = visionResult;
    }

    public Task<LlmResult> CompleteJsonAsync(
        string system, string user, int maxTokens = 500, CancellationToken ct = default)
        => Task.FromResult(_result);

    public Task<LlmResult> CompleteVisionAsync(
        string system, byte[] imageBytes, int maxTokens = 500, CancellationToken ct = default)
        => _visionResult.HasValue
            ? Task.FromResult(_visionResult.Value)
            : throw new NotSupportedException("FakeLlmClient has no vision result configured.");
}
