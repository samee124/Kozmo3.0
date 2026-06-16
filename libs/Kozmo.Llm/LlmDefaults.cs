namespace Kozmo.Llm;

/// <summary>
/// Single source of truth for model and temperature used by both record (seed-prep)
/// and replay (demo runtime). Both sides must hash the SAME key or the cache misses.
/// Cannot live in Kozmo.Llm.OpenAi because the demo runtime is banned from importing it.
/// </summary>
public static class LlmDefaults
{
    public const string Model       = "gpt-4o-mini";
    public const float  Temperature = 0f;
}
