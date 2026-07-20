using System.Text.RegularExpressions;

namespace Po.VendorCall;

/// <summary>
/// Deterministic grounding guard for LLM narrative output.
/// Extracts ISO dates (yyyy-MM-dd) and large numbers (monetary amounts, large counts)
/// from LLM text and verifies every extracted token appears in the caller-supplied
/// allowed-value set built from the fact packet.
/// No IO, no LLM, no clock reads.
/// </summary>
public static class GroundingChecker
{
    // yyyy-MM-dd
    private static readonly Regex DateRegex = new(
        @"\b\d{4}-\d{2}-\d{2}\b", RegexOptions.Compiled);

    // Comma-formatted large numbers (e.g., 285,000) OR 6+ digit numbers (e.g., 285000).
    // Small counts like "13 days" or "60 days" are intentionally not caught.
    private static readonly Regex LargeNumberRegex = new(
        @"\b(?:\d{1,3}(?:,\d{3})+|\d{6,})\b", RegexOptions.Compiled);

    /// <summary>
    /// Returns false if the LLM text contains any ISO date or large number not present
    /// in <paramref name="allowedValues"/>. Returns true for empty text or text with no
    /// checkable tokens (pass-through).
    /// </summary>
    public static bool Passes(string llmText, IReadOnlySet<string> allowedValues)
    {
        foreach (Match m in DateRegex.Matches(llmText))
        {
            if (!allowedValues.Contains(m.Value))
                return false;
        }

        foreach (Match m in LargeNumberRegex.Matches(llmText))
        {
            var normalised = m.Value.Replace(",", "");
            if (!allowedValues.Any(v => v.Replace(",", "") == normalised))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Builds an allowed-value set by extracting all ISO dates and large numbers from
    /// a collection of fact strings (values from the fact packet that were given to the LLM).
    /// Returned strings are normalised (commas stripped from numbers).
    /// </summary>
    public static HashSet<string> BuildAllowed(IEnumerable<string> factStrings)
    {
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in factStrings)
        {
            foreach (Match m in DateRegex.Matches(s))
                allowed.Add(m.Value);

            foreach (Match m in LargeNumberRegex.Matches(s))
                allowed.Add(m.Value.Replace(",", ""));
        }
        return allowed;
    }
}
