namespace Ii.Completeness;

/// <summary>
/// Deterministic bank selection: given a vendor category and maximum depth level,
/// returns the ordered set of questions that defines "complete" for that vendor.
/// Selection includes all questions at depth ≤ maxDepth (the ladder: L2 gets L1+L2).
/// Same input → same set, same order, every time.
/// </summary>
public static class QuestionSelector
{
    private static readonly IReadOnlyDictionary<string, IReadOnlyList<Question>> Banks =
        new Dictionary<string, IReadOnlyList<Question>>(StringComparer.OrdinalIgnoreCase)
        {
            [SaasQuestionBank.Category] = SaasQuestionBank.All,
        };

    public static IReadOnlyList<Question> Select(string category, DepthLevel maxDepth)
    {
        if (!Banks.TryGetValue(category, out var all))
            return [];

        return [.. all
            .Where(q => q.DepthLevel <= maxDepth)
            .OrderBy(q => q.Dimension)
            .ThenBy(q => q.DepthLevel)
            .ThenBy(q => q.Id, StringComparer.Ordinal)];
    }
}
