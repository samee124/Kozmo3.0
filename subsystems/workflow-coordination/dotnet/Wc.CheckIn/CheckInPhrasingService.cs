using Kozmo.Contracts.Interfaces;
using Kozmo.Llm;
using System.Text;
using System.Text.Json;
using Wc.Contracts;

namespace Wc.CheckIn;

using CheckIn = global::Wc.Contracts.CheckIn;

/// <summary>
/// Three-part pipeline for evidence-aware check-in phrasing.
///
/// Part 1 — BuildEvidenceContextAsync: database read, deterministic, no LLM.
/// Part 2 — BuildAnswerOptions:        pure function, deterministic, no LLM.
/// Part 3 — PhraseAsync:               live LLM call with 4-second timeout and full fallback.
///
/// HARD INVARIANTS (enforced here, not by the type system):
///   1. Claim_key is NEVER derived from or changed by the LLM — it comes from the fixed check-in.
///   2. Answer option values are set in Part 2 before the LLM call. PhraseAsync cannot alter them.
///   3. LLM failure (exception, timeout, cache miss, empty output) NEVER blocks the send.
///      PhraseAsync always returns the fixed question on any failure.
/// </summary>
public static class CheckInPhrasingService
{
    private const int PhrasingTimeoutMs = 4_000;

    // ── Part 1: evidence context ───────────────────────────────────────────────

    /// <summary>
    /// Gathers all current (non-superseded) beliefs for the (vendorId, claimKey) pair.
    /// Returns EvidenceContext with HasEvidence=false when claimKey is empty or no beliefs match.
    /// Deterministic — no LLM.
    /// </summary>
    public static async Task<EvidenceContext> BuildEvidenceContextAsync(
        Guid              vendorId,
        string            claimKey,
        IEntityStore      store,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(claimKey))
            return Empty(claimKey);

        var allCurrent = await store.GetCurrentBeliefsAsync(vendorId, ct);

        var matching = allCurrent
            .Where(b => string.Equals(b.ClaimKey, claimKey, StringComparison.OrdinalIgnoreCase)
                     && b.SupersededBy == null)
            .ToList();

        if (matching.Count == 0)
            return Empty(claimKey);

        var entries = matching.Select(b =>
        {
            var source      = b.Provenance?.Locator ?? b.Criterion;
            var optionValue = ExtractOptionValue(b.Derivation);
            var displayText = BuildDisplayText(b.Derivation, optionValue, b.Value);
            return new EvidenceEntry(displayText, optionValue, source, b.SourceTier, b.Confidence);
        }).ToList();

        return new EvidenceContext(claimKey, entries, HasEvidence: true);
    }

    // ── Part 2: answer options ─────────────────────────────────────────────────

    /// <summary>
    /// Builds the set of selectable answer options from evidence context.
    ///
    /// YES_NO:   always returns empty — evidence context informs LLM phrasing only.
    ///           The standard Yes / No / Unsure buttons are produced by the caller unchanged.
    ///
    /// TYPED_VALUE / STATUS_SELECT with evidence that has reconstructable values:
    ///   one value-option per distinct OptionValue (deduped), plus "Something else" and "Not sure"
    ///   (both link to the pending queue).
    ///
    /// TYPED_VALUE / STATUS_SELECT with no evidence or no reconstructable values:
    ///   empty — caller falls back to the existing "Answer in Kozmo →" plain path.
    ///
    /// Deterministic — no LLM.
    /// </summary>
    public static IReadOnlyList<AnswerOption> BuildAnswerOptions(
        CheckIn         checkIn,
        EvidenceContext evidenceContext)
    {
        // YES_NO: never gets value-options. Evidence context enriches LLM phrasing only.
        if (checkIn.ResponseShape == ResponseShape.YES_NO)
            return Array.Empty<AnswerOption>();

        if (!evidenceContext.HasEvidence)
            return Array.Empty<AnswerOption>();

        var options = new List<AnswerOption>();
        var seen    = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in evidenceContext.Entries)
        {
            if (entry.OptionValue is null) continue;
            if (!seen.Add(entry.OptionValue)) continue;  // dedup by value

            var label = $"{entry.OptionValue} (from {entry.Source})";
            options.Add(new AnswerOption(label, entry.OptionValue, IsOpenInput: false));
        }

        // No reconstructable values found → fall back to plain path.
        if (options.Count == 0)
            return Array.Empty<AnswerOption>();

        // Trailer options always present when there is at least one value-option.
        options.Add(new AnswerOption("Something else", Value: null, IsOpenInput: true));
        options.Add(new AnswerOption("Not sure",       Value: null, IsOpenInput: false));
        return options;
    }

    // ── Part 3: LLM phrasing with fallback ────────────────────────────────────

    /// <summary>
    /// Calls the LLM to produce a friendlier, context-aware question sentence and an optional
    /// one-line context summary ("We have X on record.").
    ///
    /// On ANY failure — LlmCacheMissException (replay mode), phrasing timeout, empty output,
    /// or any other exception — returns (fixedQuestion, null). The check-in is never blocked.
    ///
    /// The returned QuestionText replaces ONLY the displayed sentence. Claim_key, option values,
    /// and scoring are fixed before this call and are completely unaffected by its result.
    /// </summary>
    public static async Task<(string QuestionText, string? ContextSummary)> PhraseAsync(
        string            fixedQuestion,
        EvidenceContext   evidenceContext,
        IKozmoLlm         llm,
        CancellationToken ct = default)
    {
        if (!evidenceContext.HasEvidence)
            return (fixedQuestion, null);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(PhrasingTimeoutMs);

        try
        {
            var (system, user) = BuildPhrasingPrompt(fixedQuestion, evidenceContext);
            var result = await llm.CompleteJsonAsync(system, user, maxTokens: 150, cts.Token);

            string? question = null;
            string? summary  = null;

            if (result.Answer is JsonElement je)
            {
                if (je.TryGetProperty("question", out var qp) && qp.ValueKind == JsonValueKind.String)
                    question = qp.GetString()?.Trim();
                if (je.TryGetProperty("context_summary", out var sp) && sp.ValueKind == JsonValueKind.String)
                    summary = sp.GetString()?.Trim();
            }

            if (string.IsNullOrWhiteSpace(question))
                return (fixedQuestion, null);

            return (question, string.IsNullOrWhiteSpace(summary) ? null : summary);
        }
        catch (LlmCacheMissException)
        {
            // Replay/demo mode: cache miss → fixed question. Confirm page / email still sends.
            return (fixedQuestion, null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Our phrasing timeout fired (4 s), not the caller's CancellationToken → fixed question.
            return (fixedQuestion, null);
        }
        catch
        {
            // Any other LLM error → fixed question. Never blocks the send.
            return (fixedQuestion, null);
        }
    }

    // ── Private helpers ────────────────────────────────────────────────────────

    private static EvidenceContext Empty(string claimKey)
        => new(claimKey, Array.Empty<EvidenceEntry>(), HasEvidence: false);

    /// <summary>
    /// Extracts the original raw response value from a check-in answer derivation string.
    /// Check-in answer format: <c>Check-in answer to "{question}": {responseValue}</c>
    /// Returns null for other derivation formats (rule-labelled, document-sourced, etc.).
    /// </summary>
    public static string? ExtractOptionValue(string? derivation)
    {
        if (string.IsNullOrWhiteSpace(derivation)) return null;

        // The format ends with '": {responseValue}' after the closing quote of the question text.
        const string marker = "\": ";
        var idx = derivation.LastIndexOf(marker, StringComparison.Ordinal);
        if (idx < 0) return null;

        var candidate = derivation[(idx + marker.Length)..].Trim();
        if (string.IsNullOrWhiteSpace(candidate)) return null;

        // Reject values that look like long prose or contain newlines — not re-scoreable.
        if (candidate.Contains('\n') || candidate.Length > 100) return null;

        return candidate;
    }

    private static string BuildDisplayText(string? derivation, string? optionValue, double rubricScore)
    {
        if (!string.IsNullOrWhiteSpace(derivation)) return derivation;
        if (optionValue is not null) return optionValue;
        return rubricScore.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
    }

    private static (string system, string user) BuildPhrasingPrompt(
        string fixedQuestion, EvidenceContext context)
    {
        const string system =
            "You generate concise, friendly vendor check-in questions for procurement teams. " +
            "Given a base question and existing evidence, rephrase it to be specific and human. " +
            "Rules: do NOT invent facts; use ONLY the provided evidence values; do NOT include answer choices; " +
            "one sentence only. " +
            "Return JSON with exactly two fields: " +
            "\"question\" (the rephrased sentence, or the base question verbatim if no good rephrasing exists) " +
            "and \"context_summary\" (one short sentence summarising what we already have, " +
            "e.g. 'We have 99.5% uptime on record from the Q3 contract.'; null if nothing useful to summarise).";

        var sb = new StringBuilder();
        sb.AppendLine($"Base question: {fixedQuestion}");
        sb.AppendLine("Existing evidence:");
        foreach (var e in context.Entries)
            sb.AppendLine($"  - {e.DisplayText} (source: {e.Source}, tier: {e.Tier})");

        return (system, sb.ToString());
    }
}
