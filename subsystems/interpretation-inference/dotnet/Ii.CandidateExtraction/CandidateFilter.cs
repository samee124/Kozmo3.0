namespace Ii.CandidateExtraction;

/// <summary>
/// Whether the filter accepted, stripped, or dropped a raw candidate name.
/// </summary>
public enum FilterVerdict
{
    /// <summary>Raw name is a clean org name — passes through unchanged.</summary>
    Accepted,
    /// <summary>A document-structure prefix was stripped; <see cref="FilterOutcome.CleanedName"/> is the remainder.</summary>
    PrefixStripped,
    /// <summary>Candidate is form junk, checkbox text, or table noise — dropped with a reason.</summary>
    Dropped,
}

/// <summary>
/// The result of applying <see cref="CandidateFilter.Apply"/> to one raw candidate name.
/// </summary>
public sealed record FilterOutcome(
    string        RawInput,
    FilterVerdict Verdict,
    /// <summary>Non-null when <see cref="Verdict"/> is Accepted or PrefixStripped.</summary>
    string?       CleanedName,
    /// <summary>Non-null when <see cref="Verdict"/> is Dropped.</summary>
    string?       DropReason
);

/// <summary>
/// Deterministic post-filter that runs on raw candidate names returned by an upstream extractor
/// (e.g. an LLM) BEFORE they reach identity resolution. Guarantees that document-structure noise
/// and form/checkbox junk never reach Stage A.
///
/// Rules applied in order:
///   1. Empty name → Dropped.
///   2. W9 checkbox artifact (" n " marker + form-type word) → Dropped.
///   3. Junk starts-with pattern → Dropped.
///   4. Structure-prefix strip (longest match first): if a match is found, strip the prefix
///      and return the remainder; if the remainder starts with a <see cref="CandidateFilterConfig.GenericLeadWords">generic word</see>
///      or is empty → Dropped; otherwise → PrefixStripped.
///   5. No rule fired → Accepted.
///
/// The filter is deterministic, stateless, and config-driven. All lists in
/// <see cref="CandidateFilterConfig.Default"/> are ordered for correctness; caller-supplied
/// configs are sorted longest-first at construction time.
/// </summary>
public sealed class CandidateFilter
{
    private readonly CandidateFilterConfig  _config;
    // Sorted longest-first so a more-specific prefix wins over a shorter one.
    private readonly IReadOnlyList<string>  _prefixesSorted;
    private readonly HashSet<string>        _genericLeadWords;

    public CandidateFilter(CandidateFilterConfig? config = null)
    {
        _config          = config ?? CandidateFilterConfig.Default;
        _prefixesSorted  = _config.StructurePrefixes
                               .OrderByDescending(p => p.Length)
                               .ToList();
        _genericLeadWords = new HashSet<string>(
            _config.GenericLeadWords, StringComparer.OrdinalIgnoreCase);
    }

    // ── Public API ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Filters a single raw candidate name. Never throws; always returns a result.
    /// </summary>
    public FilterOutcome Apply(string rawName)
    {
        var s = rawName.Trim();
        if (s.Length == 0)
            return Dropped(rawName, "empty name");

        // Rule 2: W9 checkbox artifact.
        // PDF form checkboxes render as " n " (unchecked) in linearised text.
        // These appear exclusively in W9-style forms alongside entity-type words.
        if (IsW9CheckboxArtifact(s))
            return Dropped(rawName, "w9 form checkbox text");

        // Rule 3: Junk starts-with patterns (always drop — no strip attempt).
        foreach (var junk in _config.JunkStartsWithPatterns)
        {
            if (s.StartsWith(junk, StringComparison.OrdinalIgnoreCase))
                return Dropped(rawName, $"form or document junk (starts with '{junk}')");
        }

        // Rule 4: Structure-prefix strip.
        foreach (var prefix in _prefixesSorted)
        {
            if (!s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                continue;

            // Require that the prefix ends at a word boundary (space / comma / period /
            // end of string) to prevent "INVOICENT Corp" matching "INVOICE".
            int len = prefix.Length;
            if (len < s.Length)
            {
                char next = s[len];
                if (next != ' ' && next != ',' && next != '.' && next != '\t')
                    continue;
            }

            var remainder = s[len..].TrimStart(' ', ',', '.', '\t').Trim();

            if (remainder.Length == 0)
                return Dropped(rawName, $"structure-only name (prefix '{prefix}', nothing follows)");

            // Check if the first word of the remainder is still junk.
            var firstWord = FirstWord(remainder);
            if (_genericLeadWords.Contains(firstWord))
                return Dropped(rawName,
                    $"table fragment or field label — '{firstWord}' after stripping '{prefix}'");

            return new FilterOutcome(rawName, FilterVerdict.PrefixStripped, remainder, null);
        }

        // Rule 5: No rule fired — name passes unchanged.
        return new FilterOutcome(rawName, FilterVerdict.Accepted, s, null);
    }

    /// <summary>
    /// Filters a collection of raw names. Preserves order; Dropped outcomes are included.
    /// </summary>
    public IReadOnlyList<FilterOutcome> ApplyAll(IEnumerable<string> rawNames)
        => rawNames.Select(Apply).ToList();

    /// <summary>
    /// Filters a collection and collapses trivial duplicates: if two accepted/stripped
    /// outcomes clean to the same name (case-insensitive), only the first is kept.
    /// Dropped outcomes are always included for diagnostics.
    /// </summary>
    public IReadOnlyList<FilterOutcome> ApplyAndDedup(IEnumerable<string> rawNames)
    {
        var seen   = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<FilterOutcome>();

        foreach (var raw in rawNames)
        {
            var outcome = Apply(raw);
            if (outcome.Verdict == FilterVerdict.Dropped)
            {
                result.Add(outcome);
            }
            else if (seen.Add(outcome.CleanedName!))
            {
                result.Add(outcome);
            }
            // else: duplicate cleaned name within this document — silently skip.
        }
        return result;
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private static FilterOutcome Dropped(string raw, string reason)
        => new(raw, FilterVerdict.Dropped, null, reason);

    /// <summary>
    /// Detects the W9 checkbox artifact: " n " (unchecked checkbox in linearised PDF)
    /// combined with at least one form-entity-type word confirms this is a W9 form row,
    /// not an organisation name.
    /// </summary>
    private static bool IsW9CheckboxArtifact(string s)
    {
        if (!s.Contains(" n ", StringComparison.Ordinal)) return false;
        return s.Contains("Corp",           StringComparison.OrdinalIgnoreCase)
            || s.Contains("LLC",            StringComparison.OrdinalIgnoreCase)
            || s.Contains("Individual",     StringComparison.OrdinalIgnoreCase)
            || s.Contains("Classification", StringComparison.OrdinalIgnoreCase)
            || s.Contains("Partnership",    StringComparison.OrdinalIgnoreCase);
    }

    private static string FirstWord(string s)
    {
        var idx = s.IndexOfAny([' ', '\t', ',', '.']);
        return idx < 0 ? s : s[..idx].TrimEnd('.', ',');
    }
}
