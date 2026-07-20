using System.Text;
using System.Text.Json;
using Kozmo.Contracts;
using Kozmo.Llm;
using Rm.Contracts;

namespace Rm.Query;

/// <summary>
/// Step 3 of the query pipeline: phrase the RetrievedContext as readable prose.
///
/// LLM path: sends a strictly-grounded prompt containing only the retrieved sections.
/// The LLM is instructed to phrase â€” not generate â€” facts. It returns {"text": "..."}.
///
/// Template fallback (always correct, never invented): used when:
///   - No LLM is configured (null)
///   - LlmCacheMissException (replay mode, no cassette entry)
///   - Timeout (> 4 seconds)
///   - Any other exception
///   - LLM returns empty or unparseable text
///
/// A phrasing failure MUST NOT block an answer or invent content.
/// When ctx.FilterDimension is set, both the LLM prompt and the template fallback
/// scope their output to that single dimension's score and supporting evidence.
/// </summary>
public sealed class VendorQueryComposer
{
    private readonly IKozmoLlm? _llm;

    public VendorQueryComposer(IKozmoLlm? llm = null)
    {
        _llm = llm;
    }

    public async Task<string> ComposeAsync(
        RetrievedContext  ctx,
        Aspect            aspect,
        string            rawQuestion,
        CancellationToken ct = default)
    {
        if (_llm is null) return TemplateFallback(ctx, aspect);

        var system = BuildSystemPrompt();
        var user   = BuildUserPrompt(ctx, aspect, rawQuestion);

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(4));

            var result = await _llm.CompleteJsonAsync(system, user, maxTokens: 700, cts.Token);

            if (result.Answer is JsonElement el
             && el.TryGetProperty("text", out var textProp))
            {
                var text = textProp.GetString();
                if (!string.IsNullOrWhiteSpace(text)) return text;
            }

            return TemplateFallback(ctx, aspect);
        }
        catch (LlmCacheMissException)                                  { return TemplateFallback(ctx, aspect); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return TemplateFallback(ctx, aspect); }
        catch                                                          { return TemplateFallback(ctx, aspect); }
    }

    // â”€â”€ LLM prompt â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    private static string BuildSystemPrompt() =>
        """
        You are Kozmo, a vendor intelligence assistant. Phrase retrieved data as a clear, concise answer.

        HARD RULES â€” violations are unacceptable:
        1. Every fact you state MUST come from the "Retrieved data" sections provided below. Do not add, infer, speculate, or use your own knowledge about vendors or industries.
        2. If a section has no data, say that data is not available for that category. Do not fill in plausible values.
        3. Do not re-evaluate, recompute, or second-guess the stored posture, scores, or stance.
        4. If a section shows a contradiction, report it exactly as described â€” do not resolve or adjudicate it.
        5. Return ONLY valid JSON in this exact shape: {"text": "<your answer here>"}
        6. The "text" value must be plain readable prose. No JSON inside it. No markdown code blocks.
        """;

    private static string BuildUserPrompt(RetrievedContext ctx, Aspect aspect, string rawQuestion)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"The user asked: \"{rawQuestion}\"");
        sb.AppendLine();
        sb.AppendLine("Retrieved data (these are the ONLY facts you may use):");
        sb.AppendLine();

        // Dimension focus header when scoped
        if (ctx.FilterDimension is not null)
        {
            sb.AppendLine($"DIMENSION FOCUS: {ctx.FilterDimension.Value} (answer covers this dimension only)");
            sb.AppendLine();
        }

        // Posture always included (it's the anchor)
        if (ctx.Posture is not null && ctx.Index is not null)
        {
            sb.AppendLine($"POSTURE: {ctx.Posture.Stance} | Band: {ctx.Index.Band} | Confidence: {ctx.Posture.Confidence:P0}");
            sb.AppendLine($"Rationale: {ctx.Posture.Rationale}");
            if (ctx.Posture.Cautions.Count > 0)
                sb.AppendLine($"Cautions: {string.Join("; ", ctx.Posture.Cautions)}");
            sb.AppendLine();

            sb.AppendLine("DIMENSION SCORES:");
            if (ctx.FilterDimension is null)
            {
                // Full view: all four dimensions
                foreach (var kv in ctx.Index.DimensionScores.OrderBy(d => d.Key.ToString()))
                    sb.AppendLine($"  {kv.Key}: {kv.Value.Score:F2} (confidence {kv.Value.Confidence:F2})");
            }
            else if (ctx.Index.DimensionScores.TryGetValue(ctx.FilterDimension.Value, out var ds))
            {
                // Dimension-scoped view: one dimension only
                sb.AppendLine($"  {ctx.FilterDimension.Value}: {ds.Score:F2} (confidence {ds.Confidence:F2})");
            }
            sb.AppendLine();
        }

        // Evidence beliefs
        if (ctx.Beliefs.Count > 0)
        {
            sb.AppendLine($"EVIDENCE BELIEFS ({ctx.Beliefs.Count}):");
            foreach (var b in ctx.Beliefs.Take(12))
            {
                var summary = string.IsNullOrWhiteSpace(b.ReasoningSummary) ? "" : $" â€” {b.ReasoningSummary}";
                sb.AppendLine($"  [{b.Dimension}] {b.Criterion}: {b.Value:F2} ({b.SourceTier}, conf {b.Confidence:F2}){summary}");
            }
            sb.AppendLine();
        }
        else if (aspect is Aspect.Evidence or Aspect.Full)
        {
            sb.AppendLine("EVIDENCE BELIEFS: none on record.");
            sb.AppendLine();
        }

        // Evidence gaps from posture
        if (ctx.Posture?.EvidenceGaps.Count > 0)
        {
            sb.AppendLine("EVIDENCE GAPS:");
            foreach (var g in ctx.Posture.EvidenceGaps)
                sb.AppendLine($"  â€¢ {g}");
            sb.AppendLine();
        }

        // Open check-ins (pending owner responses)
        if (ctx.OpenCheckIns.Count > 0)
        {
            sb.AppendLine($"OPEN QUESTIONS AWAITING RESPONSE ({ctx.OpenCheckIns.Count}):");
            foreach (var ci in ctx.OpenCheckIns)
                sb.AppendLine($"  â€¢ [{ci.Kind}] {ci.Question}");
            sb.AppendLine();
        }

        // Meta gaps
        if (ctx.Gaps.Count > 0)
        {
            sb.AppendLine($"DETECTED GAPS ({ctx.Gaps.Count}):");
            foreach (var g in ctx.Gaps)
                sb.AppendLine($"  [{g.Dimension}] {g.Description}");
            sb.AppendLine();
        }

        // Contradictions
        if (ctx.Contradictions.Count > 0)
        {
            sb.AppendLine($"CONTRADICTIONS ({ctx.Contradictions.Count}):");
            foreach (var c in ctx.Contradictions)
                sb.AppendLine($"  [{c.Dimension}] {c.Description} (Severity: {c.Severity})");
            sb.AppendLine();
        }
        else if (aspect is Aspect.Contradictions or Aspect.Full)
        {
            sb.AppendLine("CONTRADICTIONS: none detected.");
            sb.AppendLine();
        }

        if (!string.IsNullOrWhiteSpace(ctx.EpistemicSummary))
        {
            sb.AppendLine($"EPISTEMIC SUMMARY: {ctx.EpistemicSummary}");
            sb.AppendLine();
        }

        if (ctx.FilterDimension is not null)
        {
            sb.AppendLine($"Phrase a clear answer focused on the {ctx.FilterDimension.Value} dimension only.");
            sb.AppendLine("Mention the dimension score, its evidence beliefs, and any gaps or contradictions for that dimension.");
            sb.AppendLine("Include the overall posture as context. Do not add any facts not shown above.");
        }
        else
        {
            sb.AppendLine("Phrase a clear answer using only the sections above.");
            sb.AppendLine("Cover: posture + rationale, then evidence if relevant, then gaps, then contradictions.");
            sb.AppendLine("Omit empty sections with a brief note. Do not add any facts not shown above.");
        }
        return sb.ToString();
    }

    // â”€â”€ Deterministic template fallback â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    public static string TemplateFallback(RetrievedContext ctx, Aspect aspect)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Vendor: {ctx.VendorName}");
        sb.AppendLine();

        if (ctx.Posture is null || ctx.Index is null)
        {
            sb.AppendLine("No assessment on record.");
            return sb.ToString().TrimEnd();
        }

        if (ctx.FilterDimension is not null)
        {
            // Dimension-focused output
            sb.AppendLine($"Dimension: {ctx.FilterDimension.Value}");
            if (ctx.Index.DimensionScores.TryGetValue(ctx.FilterDimension.Value, out var ds))
                sb.AppendLine($"Score: {ds.Score:F2} | Confidence: {ds.Confidence:F2}");
            sb.AppendLine($"Overall posture: {ctx.Posture.Stance} | Band: {ctx.Index.Band}");
            sb.AppendLine();

            if (ctx.Beliefs.Count > 0)
            {
                sb.AppendLine($"Evidence ({ctx.Beliefs.Count} beliefs):");
                foreach (var b in ctx.Beliefs)
                    sb.AppendLine($"  {b.Criterion}: {b.Value:F2} ({b.SourceTier})");
            }
            else
            {
                sb.AppendLine("Evidence: none on record for this dimension.");
            }

            if (ctx.Gaps.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Gaps:");
                foreach (var g in ctx.Gaps)
                    sb.AppendLine($"  â€¢ {g.Description}");
            }

            if (ctx.Contradictions.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine($"Contradictions ({ctx.Contradictions.Count}):");
                foreach (var c in ctx.Contradictions)
                    sb.AppendLine($"  â€¢ {c.Description} â€” Severity: {c.Severity}");
            }

            return sb.ToString().TrimEnd();
        }

        // Standard (non-dimension-scoped) output
        if (aspect is Aspect.Full or Aspect.Posture)
        {
            sb.AppendLine($"Posture: {ctx.Posture.Stance} | Band: {ctx.Index.Band} | Confidence: {ctx.Posture.Confidence:P0}");
            sb.AppendLine($"Rationale: {ctx.Posture.Rationale}");
            if (ctx.Posture.Cautions.Count > 0)
            {
                sb.AppendLine("Cautions:");
                foreach (var c in ctx.Posture.Cautions) sb.AppendLine($"  â€¢ {c}");
            }
            sb.AppendLine();
            sb.AppendLine("Dimension scores:");
            foreach (var kv in ctx.Index.DimensionScores.OrderBy(d => d.Key.ToString()))
                sb.AppendLine($"  {kv.Key}: {kv.Value.Score:F2}");
        }

        if (aspect is Aspect.Full or Aspect.Evidence)
        {
            sb.AppendLine();
            if (ctx.Beliefs.Count > 0)
            {
                sb.AppendLine($"Evidence ({ctx.Beliefs.Count} beliefs):");
                foreach (var b in ctx.Beliefs)
                    sb.AppendLine($"  [{b.Dimension}] {b.Criterion}: {b.Value:F2} ({b.SourceTier})");
            }
            else sb.AppendLine("Evidence: none on record.");
        }

        if (aspect is Aspect.Full or Aspect.Gaps)
        {
            sb.AppendLine();
            var totalGaps = ctx.OpenCheckIns.Count + ctx.Posture.EvidenceGaps.Count + ctx.Gaps.Count;
            if (totalGaps > 0)
            {
                sb.AppendLine("Gaps:");
                foreach (var ci in ctx.OpenCheckIns)
                    sb.AppendLine($"  â€¢ [Open question] {ci.Question}");
                foreach (var g in ctx.Posture.EvidenceGaps)
                    sb.AppendLine($"  â€¢ [Evidence gap] {g}");
                foreach (var g in ctx.Gaps)
                    sb.AppendLine($"  â€¢ [{g.Dimension}] {g.Description}");
            }
            else sb.AppendLine("Gaps: none detected.");
        }

        if (aspect is Aspect.Full or Aspect.Contradictions)
        {
            sb.AppendLine();
            if (ctx.Contradictions.Count > 0)
            {
                sb.AppendLine($"Contradictions ({ctx.Contradictions.Count}):");
                foreach (var c in ctx.Contradictions)
                    sb.AppendLine($"  â€¢ [{c.Dimension}] {c.Description} â€” Severity: {c.Severity}");
            }
            else sb.AppendLine("Contradictions: none detected.");
        }

        return sb.ToString().TrimEnd();
    }
}