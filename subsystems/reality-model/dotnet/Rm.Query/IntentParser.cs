using System.Text.Json;
using Ii.Spine;
using Kozmo.Llm;
using Rm.Contracts;

namespace Rm.Query;

/// <summary>
/// Step 1 of the query pipeline: parse a raw question into (vendorId, aspect).
///
/// Rules-first: exact/substring match of canonical vendor names from the EntityRegistry.
/// Aspect detection from keywords (evidence/why → Evidence; gap/missing → Gaps;
/// contradiction/conflict → Contradictions; posture/stance/status → Posture; default Full).
///
/// LLM fallback: called ONLY when rules cannot identify a vendor. The LLM returns
/// strict JSON {vendorName, aspect} and is used for parameter extraction only — it never
/// answers the question itself. On any JSON or resolution failure the result is "no vendor".
/// </summary>
public sealed class IntentParser
{
    private readonly EntityRegistry _registry;
    private readonly IKozmoLlm?     _llm;

    // Words too short or too generic to reliably identify a vendor
    private static readonly HashSet<string> IgnoredWords = new(StringComparer.OrdinalIgnoreCase)
        { "Inc.", "Ltd.", "LLC", "GmbH", "Co.", "Corp.", "AG", "and", "the", "for" };

    public sealed record ParseResult(
        Guid?                    VendorId,
        string?                  VendorName,
        Aspect                   Aspect,
        IReadOnlyList<string>    CandidateNames);

    public IntentParser(EntityRegistry registry, IKozmoLlm? llm = null)
    {
        _registry = registry;
        _llm      = llm;
    }

    public async Task<ParseResult> ParseAsync(string rawText, CancellationToken ct = default)
    {
        var aspect = DetectAspect(rawText);

        // Rule 1: find vendor(s) by matching canonical-name words in the raw text
        var candidates = FindCandidates(rawText);

        if (candidates.Count == 1)
            return new ParseResult(candidates[0].Id, candidates[0].Name, aspect, []);

        if (candidates.Count > 1)
            // Ambiguous — surface all matches so the caller can prompt the user to be specific
            return new ParseResult(null, null, aspect, candidates.Select(c => c.Name).ToList());

        // Rule 2: no match — try LLM fallback if available
        if (_llm is not null)
        {
            var llmResult = await TryLlmParseAsync(rawText, aspect, ct);
            if (llmResult is not null) return llmResult;
        }

        return new ParseResult(null, null, aspect, []);
    }

    // ── Aspect detection ──────────────────────────────────────────────────────

    private static Aspect DetectAspect(string rawText)
    {
        var t = rawText;
        if (ContainsAny(t, "evidence", "why", "reason", "because", "justify", "show me why"))
            return Aspect.Evidence;
        if (ContainsAny(t, "gap", "missing", "incomplete", "what's needed", "lacking", "coverage"))
            return Aspect.Gaps;
        if (ContainsAny(t, "contradiction", "conflict", "inconsistent", "contradicts", "conflicting"))
            return Aspect.Contradictions;
        if (ContainsAny(t, "posture", "stance", "status", "overall", "summary", "assessment"))
            return Aspect.Posture;
        return Aspect.Full;
    }

    private static bool ContainsAny(string text, params string[] keywords) =>
        keywords.Any(k => text.Contains(k, StringComparison.OrdinalIgnoreCase));

    // ── Vendor-name matching ──────────────────────────────────────────────────

    private List<(Guid Id, string Name)> FindCandidates(string rawText)
    {
        // Exact match takes priority — avoids generic-word ambiguity (e.g. "Systems")
        foreach (var id in _registry.GetAllIds())
        {
            var entity = _registry.GetEntity(id);
            if (entity is null) continue;
            if (rawText.Equals(entity.CanonicalName, StringComparison.OrdinalIgnoreCase))
                return [(id, entity.CanonicalName)];
        }

        var results = new List<(Guid, string)>();
        foreach (var id in _registry.GetAllIds())
        {
            var entity = _registry.GetEntity(id);
            if (entity is null) continue;

            var significantWords = entity.CanonicalName
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Where(w => w.TrimEnd('.', ',') is { Length: >= 4 } trimmed
                         && !IgnoredWords.Contains(w))
                .Select(w => w.TrimEnd('.', ','));

            if (significantWords.Any(w => rawText.Contains(w, StringComparison.OrdinalIgnoreCase)))
                results.Add((id, entity.CanonicalName));
        }
        return results;
    }

    // ── LLM fallback ─────────────────────────────────────────────────────────

    private async Task<ParseResult?> TryLlmParseAsync(
        string rawText, Aspect detectedAspect, CancellationToken ct)
    {
        const string system =
            """
            Extract the vendor name and question type from the user's message.
            Return ONLY valid JSON in this exact shape — nothing else:
            {"vendorName": "<name or empty string if unclear>", "aspect": "<Full|Posture|Evidence|Gaps|Contradictions>"}
            Do not answer the question. Return parameters only.
            """;

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(4));

            var result = await _llm!.CompleteJsonAsync(system, rawText, maxTokens: 60, cts.Token);

            if (result.Answer is not JsonElement el) return null;

            var vendorName = el.TryGetProperty("vendorName", out var vp) ? vp.GetString() : null;
            var aspectStr  = el.TryGetProperty("aspect",     out var ap) ? ap.GetString() : null;

            if (string.IsNullOrWhiteSpace(vendorName)) return null;

            // Try to resolve the LLM-returned name against the registry
            var candidates = FindCandidatesByName(vendorName);
            if (candidates.Count == 1)
            {
                var aspect = Enum.TryParse<Aspect>(aspectStr, ignoreCase: true, out var a) ? a : detectedAspect;
                return new ParseResult(candidates[0].Id, candidates[0].Name, aspect, []);
            }

            return null; // couldn't resolve or ambiguous
        }
        catch (LlmCacheMissException)    { return null; }
        catch (OperationCanceledException) { return null; }
        catch                            { return null; }
    }

    private List<(Guid Id, string Name)> FindCandidatesByName(string name)
    {
        var results = new List<(Guid, string)>();
        foreach (var id in _registry.GetAllIds())
        {
            var entity = _registry.GetEntity(id);
            if (entity is null) continue;
            if (entity.CanonicalName.Contains(name, StringComparison.OrdinalIgnoreCase)
             || name.Contains(entity.CanonicalName.Split(' ')[0], StringComparison.OrdinalIgnoreCase))
                results.Add((id, entity.CanonicalName));
        }
        return results;
    }
}
