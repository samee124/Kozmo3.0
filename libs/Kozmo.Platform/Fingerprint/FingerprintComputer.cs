using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kozmo.Platform.Fingerprint;

/// <summary>
/// Deterministic SHA-256 fingerprint over sorted beliefs ⊕ dimension scores ⊕ weights ⊕ config_version.
/// Same input → byte-identical output every run. No clock, no randomness.
/// </summary>
public static class FingerprintComputer
{
    private static readonly JsonSerializerOptions SerOptions = new()
    {
        WriteIndented          = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        PropertyNamingPolicy   = JsonNamingPolicy.SnakeCaseLower,
        NumberHandling         = JsonNumberHandling.Strict,
    };

    public static string Compute(FingerprintInput input)
    {
        var stable = new StableFingerprintInput(
            Beliefs:          [.. input.Beliefs
                                    .OrderBy(b => b.Dimension, StringComparer.Ordinal)
                                    .ThenBy(b => b.Criterion,  StringComparer.Ordinal)
                                    .Select(b => new BeliefSnapshot(b.Dimension, b.Criterion,
                                                                    Round6(b.Value), Round6(b.Confidence)))],
            DimensionScores:  input.DimensionScores
                                    .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                                    .ToDictionary(kv => kv.Key, kv => Round6(kv.Value)),
            DimensionWeights: input.DimensionWeights
                                    .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                                    .ToDictionary(kv => kv.Key, kv => Round6(kv.Value)),
            ConfigVersion:    input.ConfigVersion
        );

        var json  = JsonSerializer.Serialize(stable, SerOptions);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(json));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static double Round6(double v) => Math.Round(v, 6, MidpointRounding.AwayFromZero);

    // Private stable DTO — sorted dictionaries serialise to arrays to eliminate key-order drift
    private sealed record StableFingerprintInput(
        IReadOnlyList<BeliefSnapshot>           Beliefs,
        IReadOnlyDictionary<string, double>     DimensionScores,
        IReadOnlyDictionary<string, double>     DimensionWeights,
        string                                  ConfigVersion
    );
}
