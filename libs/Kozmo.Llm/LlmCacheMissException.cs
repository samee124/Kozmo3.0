namespace Kozmo.Llm;

/// <summary>
/// Thrown by <see cref="CachingLlmClient"/> in replay mode when the cache does not contain
/// an entry for the requested prompt. Run seed-prep (record mode) to populate the cache.
/// </summary>
public sealed class LlmCacheMissException : Exception
{
    public string CacheKey { get; }

    public LlmCacheMissException(string cacheKey)
        : base($"LLM cache miss for key '{cacheKey}'. " +
               "Run seed-prep in record mode to populate the cache before running the demo.")
    {
        CacheKey = cacheKey;
    }
}
