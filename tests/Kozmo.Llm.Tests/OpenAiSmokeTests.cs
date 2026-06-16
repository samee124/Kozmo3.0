using System.Text.Json;
using Kozmo.Llm.OpenAi;
using Xunit;

namespace Kozmo.Llm.Tests;

/// <summary>
/// Live smoke test — skipped automatically when OPENAI_API_KEY is absent.
/// Run manually from seed-prep / CI smoke gate with the key set.
/// These tests are NOT part of the normal offline test run.
/// </summary>
public sealed class OpenAiSmokeTests
{
    [Fact]
    public async Task RealClient_RoundTrips_OneJsonPrompt()
    {
        var apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (string.IsNullOrEmpty(apiKey))
            return; // no key — skip gracefully in offline/demo runs

        var client = new OpenAiLlmClient();
        var result = await client.CompleteJsonAsync(
            system: "You are a test assistant. Always respond with exactly the JSON object {\"ok\":true}.",
            user:   "Confirm.",
            maxTokens: 50);

        Assert.NotNull(result.Answer);
        var elem = Assert.IsType<JsonElement>(result.Answer);
        Assert.Equal(JsonValueKind.Object, elem.ValueKind);
    }
}
