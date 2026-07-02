using System.Text.RegularExpressions;
using Ig.Contracts;

namespace Ig.Resolution;

/// <summary>
/// Stage A — Normalize. Deterministic, no I/O.
/// Produces a comparison_key from raw_name; raw_name is preserved untouched.
/// </summary>
public static class Normalizer
{
    // Expand CamelCase before lowercasing so "CloudWave" → "Cloud Wave"
    private static readonly Regex _camelExpand = new(
        @"([a-z])([A-Z])", RegexOptions.Compiled);

    // Strip everything that is not a letter, digit, or space (applied after lowercasing)
    private static readonly Regex _nonAlphaNum = new(
        @"[^a-z0-9\s]", RegexOptions.Compiled);

    // Legal suffixes stripped as whole words (word-boundary anchored, case-insensitive).
    // "company" is included so "Widget Company" and "Widget Co." produce the same key.
    private static readonly Regex _legalSuffixes = new(
        @"\b(inc|llc|ltd|limited|company|gmbh|corp|co|sa|bv|ag|plc)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Noise/stop words stripped as whole words
    private static readonly Regex _noiseWords = new(
        @"\b(the|a|an|of|and)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Document-title prefix: "Amendment 3 –", "SOW –", "Agreement:", etc.
    // Handles en-dash (–, U+2013), em-dash (—, U+2014), hyphen, and colon as separator.
    private static readonly Regex _docPrefix = new(
        @"^\s*(amendment|agreement|sow|statement\s+of\s+work|exhibit|schedule|annex|" +
        @"addendum|order\s+form|purchase\s+order|contract|nda|msa|appendix|po)\s*\d*[a-z]?\s*" +
        @"[-–—:]\s*",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static NormalizedCandidate Normalize(CandidateIdentityBelief candidate)
    {
        var raw = candidate.RawName;

        // Strip document-title prefix to get the effective name for pattern matching
        var prefixMatch = _docPrefix.Match(raw);
        var effectiveName = prefixMatch.Success
            ? raw[prefixMatch.Length..].Trim()
            : raw;

        // Compute the compact comparison key from the effective name
        var key = ComputeKey(effectiveName);

        return new NormalizedCandidate(candidate, key, effectiveName);
    }

    /// <summary>
    /// Visible for testing. Produces a compact, whitespace-free key from any name string.
    /// </summary>
    internal static string ComputeKey(string name)
    {
        // 1. Strip legal suffixes FIRST — before CamelCase expansion so "GmbH" stays whole
        var noSuffix  = _legalSuffixes.Replace(name, " ");

        // 2. Strip noise/stop words while still in original casing
        var noNoise   = _noiseWords.Replace(noSuffix, " ");

        // 3. Expand CamelCase ("CloudWave" → "Cloud Wave")
        var expanded  = _camelExpand.Replace(noNoise, "$1 $2");

        // 4. Lowercase
        var lower     = expanded.ToLowerInvariant();

        // 5. Strip punctuation (keep alpha, digits, space)
        var clean     = _nonAlphaNum.Replace(lower, " ");

        // 6. Collapse whitespace and remove all spaces → compact key
        return string.Concat(clean.Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }
}
