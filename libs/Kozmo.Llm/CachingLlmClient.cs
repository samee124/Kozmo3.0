using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kozmo.Llm;

/// <summary>
/// Record-and-replay LLM client. Two modes:
/// <list type="bullet">
///   <item><b>Replay (demo)</b>: serves results from a frozen JSON cache; throws
///     <see cref="LlmCacheMissException"/> on a miss — never falls through to the network.</item>
///   <item><b>Record (seed-prep only)</b>: on a miss, delegates to an inner client, writes the
///     result to the cache file, then returns it.</item>
/// </list>
/// Cache key is a stable SHA-256 hash of (model, temperature, system, user, maxTokens).
/// If the model or temperature changes the key changes, preventing silent replay of stale results.
/// </summary>
public sealed class CachingLlmClient : IKozmoLlm
{
    private readonly string        _cachePath;
    private readonly bool          _recordMode;
    private readonly IKozmoLlm?    _inner;
    private readonly string        _model;
    private readonly float         _temperature;
    private readonly SemaphoreSlim _lock = new(1, 1);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy        = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition      = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented               = true
    };

    /// <param name="cachePath">Path to the JSON cache file (created if absent in record mode).</param>
    /// <param name="recordMode">
    ///   When true, cache misses delegate to <paramref name="inner"/> and write to the cache.
    ///   When false (default / demo), cache misses throw <see cref="LlmCacheMissException"/>.
    /// </param>
    /// <param name="inner">Required in record mode; ignored in replay mode.</param>
    /// <param name="model">Model identifier included in the cache key. Defaults to "gpt-4o-mini".</param>
    /// <param name="temperature">Sampling temperature included in the cache key. Defaults to 0.</param>
    public CachingLlmClient(
        string     cachePath,
        bool       recordMode  = false,
        IKozmoLlm? inner       = null,
        string     model       = LlmDefaults.Model,
        float      temperature = LlmDefaults.Temperature)
    {
        if (recordMode && inner is null)
            throw new ArgumentException("Record mode requires an inner client.", nameof(inner));

        _cachePath   = cachePath;
        _recordMode  = recordMode;
        _inner       = inner;
        _model       = model;
        _temperature = temperature;
    }

    public async Task<LlmResult> CompleteJsonAsync(
        string system,
        string user,
        int    maxTokens = 500,
        CancellationToken ct = default)
    {
        var key = ComputeKey(system, user, maxTokens, _model, _temperature);

        await _lock.WaitAsync(ct);
        try
        {
            var cache = LoadCache();

            if (cache.TryGetValue(key, out var entry))
                return EntryToResult(entry);

            if (!_recordMode)
                throw new LlmCacheMissException(key);

            // record mode — call inner, persist
            var result = await _inner!.CompleteJsonAsync(system, user, maxTokens, ct);
            cache[key] = ResultToEntry(result);
            SaveCache(cache);
            return result;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Stable SHA-256 hash of (model, temperature, system, user, maxTokens).
    /// Internal so tests can verify determinism across all key dimensions.
    /// </summary>
    internal static string ComputeKey(
        string system, string user, int maxTokens, string model, float temperature)
    {
        var temp  = temperature.ToString("R", CultureInfo.InvariantCulture);
        var input = $"{model}|{temp}|{system}|{user}|{maxTokens}";
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    // ── Serialization ──────────────────────────────────────────────────────

    private Dictionary<string, CacheEntry> LoadCache()
    {
        if (!File.Exists(_cachePath))
            return new();

        var json = File.ReadAllText(_cachePath);
        if (string.IsNullOrWhiteSpace(json) || json.Trim() == "{}")
            return new();

        return JsonSerializer.Deserialize<Dictionary<string, CacheEntry>>(json, JsonOpts)
               ?? new();
    }

    private void SaveCache(Dictionary<string, CacheEntry> cache)
    {
        var json = JsonSerializer.Serialize(cache, JsonOpts);
        File.WriteAllText(_cachePath, json);
    }

    private static LlmResult EntryToResult(CacheEntry e)
    {
        object? answer = null;
        if (!string.IsNullOrEmpty(e.AnswerJson))
            answer = JsonSerializer.Deserialize<JsonElement>(e.AnswerJson);
        return new LlmResult(answer, e.Confidence, e.ReasoningSummary ?? "");
    }

    private static CacheEntry ResultToEntry(LlmResult r) => new()
    {
        AnswerJson      = r.Answer is null ? null : JsonSerializer.Serialize(r.Answer),
        Confidence      = r.Confidence,
        ReasoningSummary = r.ReasoningSummary
    };

    private sealed class CacheEntry
    {
        public string? AnswerJson       { get; init; }
        public double  Confidence       { get; init; }
        public string? ReasoningSummary { get; init; }
    }
}
