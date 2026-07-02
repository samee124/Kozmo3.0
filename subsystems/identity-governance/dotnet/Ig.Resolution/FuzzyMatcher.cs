namespace Ig.Resolution;

/// <summary>
/// Normalized Levenshtein similarity in [0, 1].
/// Used by Stage C (merge decisions) and Stage D (collision / rebrand detection).
/// Pure arithmetic — deterministic, no I/O.
/// </summary>
internal static class FuzzyMatcher
{
    /// <summary>
    /// Returns 1.0 for identical strings, 0.0 for completely disjoint strings.
    /// Score = 1 - (editDistance / max(len_a, len_b)).
    /// </summary>
    public static double Similarity(string a, string b)
    {
        if (string.Equals(a, b, StringComparison.Ordinal)) return 1.0;
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return 0.0;

        int dist   = EditDistance(a, b);
        int maxLen = Math.Max(a.Length, b.Length);
        return 1.0 - (double)dist / maxLen;
    }

    private static int EditDistance(string a, string b)
    {
        int m = a.Length;
        int n = b.Length;
        var dp = new int[m + 1, n + 1];

        for (int i = 0; i <= m; i++) dp[i, 0] = i;
        for (int j = 0; j <= n; j++) dp[0, j] = j;

        for (int i = 1; i <= m; i++)
            for (int j = 1; j <= n; j++)
                dp[i, j] = a[i - 1] == b[j - 1]
                    ? dp[i - 1, j - 1]
                    : 1 + Math.Min(dp[i - 1, j - 1],
                          Math.Min(dp[i - 1, j], dp[i, j - 1]));

        return dp[m, n];
    }
}
