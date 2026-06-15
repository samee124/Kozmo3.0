using Kozmo.Contracts;

namespace Ii.Tests;

/// <summary>
/// Test-only factory for MetaCognitionResult values.
/// Mirrors what Dev B's real metacognition pass will produce; used so A2 tests
/// compile and exercise the posture contract without a real LLM.
/// </summary>
internal static class FakeMetaCognition
{
    /// <summary>No contradictions, no gaps — the null object for metacognition.</summary>
    public static MetaCognitionResult Empty(string entityId) =>
        new(entityId, [], [], "No issues detected.");

    /// <summary>N contradictions with placeholder descriptions, zero gaps.</summary>
    public static MetaCognitionResult WithContradictions(string entityId, int count) =>
        new(entityId,
            Enumerable.Range(0, count)
                .Select(i => new Contradiction(
                    entityId,
                    "Operational",
                    $"Simulated contradiction {i + 1}: conflicting signals on the same dimension",
                    ContradictionSeverity.Medium,
                    [],
                    DetectionSource.Deterministic))
                .ToList(),
            [],
            $"{count} simulated contradiction(s) detected.");

    /// <summary>Zero contradictions, N gaps using the caller-supplied description strings.</summary>
    public static MetaCognitionResult WithGaps(string entityId, IReadOnlyList<string> descriptions) =>
        new(entityId,
            [],
            descriptions
                .Select(d => new Gap(entityId, "Operational", d, DetectionSource.Deterministic))
                .ToList(),
            $"{descriptions.Count} simulated gap(s) detected.");
}
