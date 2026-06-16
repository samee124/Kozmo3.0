namespace Kozmo.Llm.Tests;

/// <summary>Deterministic stub for testing CachingLlmClient in record mode.</summary>
internal sealed class FakeLlmClient : IKozmoLlm
{
    private readonly LlmResult _result;

    public FakeLlmClient(LlmResult result) => _result = result;

    public Task<LlmResult> CompleteJsonAsync(
        string system, string user, int maxTokens = 500, CancellationToken ct = default)
        => Task.FromResult(_result);
}
