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
/// Cache key is a stable SHA-256 hash of (model, temperature, system, user, maxTokens), with
/// line endings normalized to LF before hashing so the key is immune to CRLF/LF drift across
/// checkouts and platforms (see <see cref="ComputeKey"/>). Lookups also fall back to the
/// pre-normalization ("legacy") key so cassettes recorded before this change keep replaying
/// without a mass re-record; all new writes use the normalized key, so cassettes converge over
/// time. If the model or temperature changes the key changes, preventing silent replay of stale
/// results.
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
        var key       = ComputeKey(system, user, maxTokens, _model, _temperature);
        var legacyKey = ComputeLegacyKey(system, user, maxTokens, _model, _temperature);

        await _lock.WaitAsync(ct);
        try
        {
            var cache = LoadCache();

            if (cache.TryGetValue(key, out var entry))
                return EntryToResult(entry);

            // Backward compatibility: cassettes recorded before line-ending normalization was
            // introduced were keyed on the raw, un-normalized prompt text. Fall back to that
            // key so existing cassettes keep replaying without a re-record pass. New entries
            // are always written under the normalized key (below), so this fallback path only
            // ever matters for old data — it never masks a genuine miss on freshly recorded data.
            if (cache.TryGetValue(legacyKey, out entry))
                return EntryToResult(entry);

            if (!_recordMode)
                throw new LlmCacheMissException(key);

            // record mode — call inner, persist under the normalized key
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

    public async Task<LlmResult> CompleteVisionAsync(
        string            system,
        byte[]            imageBytes,
        int               maxTokens = 500,
        CancellationToken ct        = default)
    {
        var key       = ComputeVisionKey(system, imageBytes, maxTokens, _model, _temperature);
        var legacyKey = ComputeLegacyVisionKey(system, imageBytes, maxTokens, _model, _temperature);

        await _lock.WaitAsync(ct);
        try
        {
            var cache = LoadCache();

            if (cache.TryGetValue(key, out var entry))
                return EntryToResult(entry);

            // See CompleteJsonAsync — same backward-compatible fallback for pre-normalization
            // vision cassette entries.
            if (cache.TryGetValue(legacyKey, out entry))
                return EntryToResult(entry);

            if (!_recordMode)
                throw new LlmCacheMissException(key);

            if (_inner is null)
                throw new InvalidOperationException("Record mode requires an inner client.");

            var result = await _inner.CompleteVisionAsync(system, imageBytes, maxTokens, ct);
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
    /// Stable SHA-256 hash of (model, temperature, system, user, maxTokens) — the key used for
    /// ALL new cassette writes. Internal so tests can verify determinism across all key dimensions.
    /// <para>
    /// Line endings in the assembled input are normalized to LF before hashing. Prompt text
    /// often originates from C# raw string literals or fixture files whose physical line-ending
    /// bytes depend on the checkout (<c>.gitattributes</c> eol rules, <c>core.autocrlf</c>) or the
    /// host OS (e.g. <see cref="JsonSerializerOptions.WriteIndented"/> emits <c>Environment.NewLine</c>).
    /// Without normalization, the exact same logical prompt hashes differently across machines or
    /// checkouts, silently breaking cassette replay. Normalizing here makes the key a function of
    /// content, not incidental line-ending encoding.
    /// </para>
    /// </summary>
    internal static string ComputeKey(
        string system, string user, int maxTokens, string model, float temperature)
    {
        var temp  = temperature.ToString("R", CultureInfo.InvariantCulture);
        var input = NormalizeLineEndings($"{model}|{temp}|{system}|{user}|{maxTokens}");
        return Hash(input);
    }

    /// <summary>
    /// Pre-normalization key, computed on the raw (un-normalized) prompt text — i.e. exactly
    /// what <see cref="ComputeKey"/> computed before line-ending normalization was introduced.
    /// Read-only lookup fallback: every cassette entry recorded before this change was written
    /// under this key. Never used to write new entries — <see cref="ComputeKey"/> is the sole
    /// write key, so cassettes converge on the normalized scheme over time without a mass
    /// re-record of already-working cassettes.
    /// </summary>
    internal static string ComputeLegacyKey(
        string system, string user, int maxTokens, string model, float temperature)
    {
        var temp  = temperature.ToString("R", CultureInfo.InvariantCulture);
        var input = $"{model}|{temp}|{system}|{user}|{maxTokens}";
        return Hash(input);
    }

    /// <summary>
    /// Stable SHA-256 hash for vision calls — the key used for ALL new vision cassette writes.
    /// The "vision|" prefix guarantees no collision with text-prompt keys even if image bytes
    /// happened to produce the same pre-hash string. Image bytes are hashed separately (not
    /// base64-embedded) to keep the input string small. The <paramref name="system"/> prompt is
    /// line-ending normalized for the same reason as <see cref="ComputeKey"/>.
    /// </summary>
    internal static string ComputeVisionKey(
        string system, byte[] imageBytes, int maxTokens, string model, float temperature)
    {
        var temp    = temperature.ToString("R", CultureInfo.InvariantCulture);
        var imgHash = Convert.ToHexString(SHA256.HashData(imageBytes)).ToLowerInvariant();
        var input   = NormalizeLineEndings($"vision|{model}|{temp}|{system}|{imgHash}|{maxTokens}");
        return Hash(input);
    }

    /// <summary>
    /// Pre-normalization vision key — see <see cref="ComputeLegacyKey"/>. Read-only lookup
    /// fallback for vision cassette entries recorded before normalization.
    /// </summary>
    internal static string ComputeLegacyVisionKey(
        string system, byte[] imageBytes, int maxTokens, string model, float temperature)
    {
        var temp    = temperature.ToString("R", CultureInfo.InvariantCulture);
        var imgHash = Convert.ToHexString(SHA256.HashData(imageBytes)).ToLowerInvariant();
        var input   = $"vision|{model}|{temp}|{system}|{imgHash}|{maxTokens}";
        return Hash(input);
    }

    /// <summary>
    /// Normalizes all line-ending styles to LF: CRLF pairs first (so they collapse to one LF,
    /// not two), then any remaining stray CR (bare old-Mac-style line endings).
    /// </summary>
    private static string NormalizeLineEndings(string s) =>
        s.Replace("\r\n", "\n").Replace("\r", "\n");

    private static string Hash(string input) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input))).ToLowerInvariant();

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
