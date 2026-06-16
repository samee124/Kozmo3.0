using System.Text.Json;
using OpenAI.Chat;

namespace Kozmo.Llm.OpenAi;

/// <summary>
/// Real OpenAI provider implementing <see cref="IKozmoLlm"/> over GPT-4o-mini.
/// Reads OPENAI_API_KEY from the environment. Never imported by the demo runtime —
/// reachable from seed-prep and smoke entrypoints only (CI Lane 5 enforces this).
/// </summary>
public sealed class OpenAiLlmClient : IKozmoLlm
{
    /// <summary>Default model used when none is supplied. Mirrors <see cref="LlmDefaults.Model"/>.</summary>
    public const string DefaultModel       = LlmDefaults.Model;

    /// <summary>
    /// Temperature is pinned to 0 for deterministic, reproducible outputs.
    /// Mirrors <see cref="LlmDefaults.Temperature"/>. Changing invalidates all cache entries.
    /// </summary>
    public const float DefaultTemperature = LlmDefaults.Temperature;

    private readonly ChatClient _chat;

    /// <summary>The model this instance is configured for.</summary>
    public string Model { get; }

    public OpenAiLlmClient(string? model = null)
    {
        var apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY")
            ?? throw new InvalidOperationException(
                "OPENAI_API_KEY environment variable is not set.");
        Model = model ?? DefaultModel;
        _chat = new ChatClient(Model, apiKey);
    }

    public async Task<LlmResult> CompleteJsonAsync(
        string system,
        string user,
        int    maxTokens = 500,
        CancellationToken ct = default)
    {
        var messages = new List<ChatMessage>
        {
            new SystemChatMessage(system),
            new UserChatMessage(user)
        };

        var options = new ChatCompletionOptions
        {
            MaxOutputTokenCount = maxTokens,
            Temperature         = DefaultTemperature,
            ResponseFormat      = ChatResponseFormat.CreateJsonObjectFormat()
        };

        ChatCompletion completion = await _chat.CompleteChatAsync(messages, options, ct);
        var text = completion.Content[0].Text;

        var answer = JsonSerializer.Deserialize<JsonElement>(text);
        return new LlmResult(answer, Confidence: 0.85, ReasoningSummary: "");
    }
}
