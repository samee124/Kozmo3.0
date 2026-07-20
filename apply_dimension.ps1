# ── 1. VendorQuery.cs — add FilterDimension ──────────────────────────────────
$vendorQueryContent = @'
using Kozmo.Contracts;

namespace Rm.Contracts;

/// <summary>
/// Channel-agnostic input to the Reality-Model Query Service.
/// Front doors (Slack, Copilot) may pre-fill ResolvedVendorId / Aspect when they can;
/// the service handles the raw-text-only case too.
///
/// FilterDimension (optional): when set, the retriever scopes beliefs, gaps, and contradictions
/// to that single dimension, and the composer phrases a dimension-focused answer.
/// Orthogonal to Aspect — combine freely.
/// </summary>
public sealed record VendorQuery(
    string     RawText,
    Guid?      ResolvedVendorId = null,
    Aspect     Aspect           = Aspect.Full,
    Dimension? FilterDimension  = null
);
'@
[System.IO.File]::WriteAllText('subsystems\reality-model\dotnet\Rm.Contracts\VendorQuery.cs', $vendorQueryContent)

# ── 2. RetrievedContext.cs — add FilterDimension field ───────────────────────
$retrievedContextContent = @'
using Kozmo.Contracts;

namespace Rm.Contracts;

/// <summary>
/// All facts retrieved from the existing read model for a single vendor + aspect.
/// This is the grounding layer: every factual claim in VendorQueryAnswer.Text must trace
/// to a field here. Callers and tests can verify the prose against the structured data.
/// </summary>
public sealed record RetrievedContext(
    Guid                              VendorId,
    string                            VendorName,

    /// <summary>False when the vendor is known but has no scored index/posture yet.</summary>
    bool                              IsAssessed,

    /// <summary>Null when IsAssessed = false.</summary>
    PostureAssignment?                Posture,

    /// <summary>Null when IsAssessed = false.</summary>
    EntityIndex?                      Index,

    /// <summary>Current (non-superseded) beliefs. Populated for Full and Evidence aspects.</summary>
    IReadOnlyList<Belief>             Beliefs,

    /// <summary>Detected contradictions with severity. Populated for Full and Contradictions aspects.</summary>
    IReadOnlyList<Contradiction>      Contradictions,

    /// <summary>Detected gaps. Populated for Full and Gaps aspects.</summary>
    IReadOnlyList<Gap>                Gaps,

    /// <summary>LLM-generated epistemic summary from the meta-cognition pass. May be null.</summary>
    string?                           EpistemicSummary,

    /// <summary>Open check-in questions awaiting owner response. Populated for Full and Gaps aspects.</summary>
    IReadOnlyList<OpenCheckInSummary> OpenCheckIns,

    /// <summary>
    /// Future seam for access-control. Passed through the retrieve step but never acted on in this phase.
    /// </summary>
    string?                           CallerContext = null,

    /// <summary>
    /// When non-null, this context was scoped to a single dimension.
    /// Beliefs, Gaps, and Contradictions contain only data for that dimension.
    /// The composer uses this to phrase a dimension-focused answer.
    /// </summary>
    Dimension?                        FilterDimension = null
);

/// <summary>Lightweight projection of an open check-in for grounding purposes.</summary>
public sealed record OpenCheckInSummary(Guid CheckInId, string Question, string Kind);
'@
[System.IO.File]::WriteAllText('subsystems\reality-model\dotnet\Rm.Contracts\RetrievedContext.cs', $retrievedContextContent)

# ── 3. VendorQueryRetriever.cs — add filterDimension param + filtering ────────
$retrieverContent = @'
using Ii.Contracts;
using Ii.Spine;
using Kozmo.Contracts;
using Rm.Contracts;
using Wc.Contracts;

namespace Rm.Query;

/// <summary>
/// Step 2 of the query pipeline: deterministic retrieval from the existing read model.
/// No LLM. No writes. Aspect controls which sections are populated.
/// FilterDimension (optional) narrows beliefs, gaps, and contradictions to one dimension.
///
/// Seam for future access control: callerContext is threaded through but never acted on.
/// </summary>
public sealed class VendorQueryRetriever
{
    private readonly IIiFacade      _facade;
    private readonly ICheckInStore  _checkInStore;
    private readonly EntityRegistry _registry;

    public VendorQueryRetriever(
        IIiFacade      facade,
        ICheckInStore  checkInStore,
        EntityRegistry registry)
    {
        _facade       = facade;
        _checkInStore = checkInStore;
        _registry     = registry;
    }

    /// <summary>
    /// Pull a RetrievedContext for the given vendor, aspect, and optional dimension filter.
    /// Returns null if the vendor ID is not in the registry (completely unknown entity).
    /// Returns a context with IsAssessed=false if the vendor is known but has no scored data.
    /// When filterDimension is set, beliefs/gaps/contradictions are scoped to that dimension only.
    /// callerContext is a no-op seam — reserved for future authorization.
    /// </summary>
    public async Task<RetrievedContext?> RetrieveAsync(
        Guid       vendorId,
        Aspect     aspect,
        Dimension? filterDimension = null,
        string?    callerContext   = null,
        CancellationToken ct       = default)
    {
        var entity = _registry.GetEntity(vendorId);
        if (entity is null) return null;

        // Single round-trip for posture + index + beliefs + meta
        var trail = await _facade.GetReasoningTrailAsync(vendorId, ct);

        bool isAssessed = trail?.Posture is not null && trail?.Index is not null;

        // Open check-ins for this vendor (gaps awaiting owner response)
        IReadOnlyList<OpenCheckInSummary> openCheckIns = [];
        if (isAssessed && aspect is Aspect.Full or Aspect.Gaps)
        {
            var allOpen = await _checkInStore.GetOpenAsync(ct);
            openCheckIns = allOpen
                .Where(c => c.VendorId == vendorId)
                .Select(c => new OpenCheckInSummary(c.CheckInId, c.Question, c.Kind.ToString()))
                .ToList();
        }

        // Raw collections from trail
        var allBeliefs        = trail?.CurrentBeliefs ?? [];
        var allContradictions = trail?.Meta?.Contradictions ?? [];
        var allGaps           = trail?.Meta?.Gaps ?? [];

        // Apply dimension filter when specified
        IReadOnlyList<Belief>        beliefs;
        IReadOnlyList<Contradiction> contradictions;
        IReadOnlyList<Gap>           gaps;

        if (filterDimension is not null)
        {
            var dimName = filterDimension.Value.ToString();
            beliefs        = allBeliefs.Where(b => b.Dimension == filterDimension).ToList();
            contradictions = allContradictions.Where(c => c.Dimension == dimName).ToList();
            gaps           = allGaps.Where(g => g.Dimension == dimName).ToList();
        }
        else
        {
            beliefs = aspect is Aspect.Full or Aspect.Evidence
                ? allBeliefs : [];
            contradictions = aspect is Aspect.Full or Aspect.Contradictions
                ? allContradictions : [];
            gaps = aspect is Aspect.Full or Aspect.Gaps
                ? allGaps : [];
        }

        return new RetrievedContext(
            VendorId:         vendorId,
            VendorName:       entity.CanonicalName,
            IsAssessed:       isAssessed,
            Posture:          trail?.Posture,
            Index:            trail?.Index,
            Beliefs:          beliefs,
            Contradictions:   contradictions,
            Gaps:             gaps,
            EpistemicSummary: trail?.Meta?.EpistemicSummary,
            OpenCheckIns:     openCheckIns,
            CallerContext:    callerContext,
            FilterDimension:  filterDimension
        );
    }
}
'@
[System.IO.File]::WriteAllText('subsystems\reality-model\dotnet\Rm.Query\VendorQueryRetriever.cs', $retrieverContent)

# ── 4. VendorQueryService.cs — pass FilterDimension; force Full when set ──────
$serviceContent = @'
using Ii.Contracts;
using Ii.Spine;
using Kozmo.Contracts;
using Kozmo.Llm;
using Rm.Contracts;
using Wc.Contracts;

namespace Rm.Query;

/// <summary>
/// Reality-Model Query Service orchestrator.
/// Implements IVendorQueryService via three sequential steps:
///   1. IntentParser  — parse raw text → (vendorId?, aspect)
///   2. VendorQueryRetriever — deterministic read-model pull → RetrievedContext
///   3. VendorQueryComposer  — LLM phrasing (or template fallback) → prose text
///
/// Invariants enforced here:
///   - Unknown vendor → honest "no such vendor" answer; compose is NOT called.
///   - Resolved but not assessed → honest "no assessment yet"; compose is NOT called.
///   - Ambiguous vendor name → list candidates; compose is NOT called.
///   - All factual content in the answer traces to Grounding (the RetrievedContext).
///   - When FilterDimension is set, aspect is forced to Full so the retriever gets all
///     sections for that dimension; the retriever then narrows to that dimension only.
/// </summary>
public sealed class VendorQueryService : IVendorQueryService
{
    private readonly IntentParser          _parser;
    private readonly VendorQueryRetriever  _retriever;
    private readonly VendorQueryComposer   _composer;

    public VendorQueryService(
        IIiFacade      facade,
        ICheckInStore  checkInStore,
        EntityRegistry registry,
        IKozmoLlm?     llm = null)
    {
        _parser    = new IntentParser(registry, llm);
        _retriever = new VendorQueryRetriever(facade, checkInStore, registry);
        _composer  = new VendorQueryComposer(llm);
    }

    /// <summary>
    /// Internal ctor for tests: inject pre-built components directly.
    /// </summary>
    internal VendorQueryService(
        IntentParser         parser,
        VendorQueryRetriever retriever,
        VendorQueryComposer  composer)
    {
        _parser    = parser;
        _retriever = retriever;
        _composer  = composer;
    }

    public async Task<VendorQueryAnswer> AnswerAsync(
        VendorQuery       query,
        CancellationToken ct = default)
    {
        Guid?  vendorId = query.ResolvedVendorId;
        Aspect aspect   = query.Aspect;

        // ── Step 1: parse (skip if caller pre-resolved the vendor) ────────────
        if (vendorId is null)
        {
            var parsed = await _parser.ParseAsync(query.RawText, ct);
            aspect   = query.Aspect == Aspect.Full ? parsed.Aspect : query.Aspect;
            vendorId = parsed.VendorId;

            if (vendorId is null)
            {
                // Ambiguous match
                if (parsed.CandidateNames.Count > 1)
                    return new VendorQueryAnswer(
                        $"I found multiple vendors matching your query: {string.Join(", ", parsed.CandidateNames)}. " +
                        "Please be more specific about which vendor you mean.",
                        null);

                // No match at all
                return new VendorQueryAnswer(
                    "I couldn't identify a vendor in your question. " +
                    "Please name the vendor you'd like to know about (e.g. \"What's Cloudwave's posture?\").",
                    null);
            }
        }

        // When a dimension filter is set, always retrieve all sections (Full) so the retriever
        // has everything it needs to filter down to that dimension's beliefs/gaps/contradictions.
        if (query.FilterDimension is not null)
            aspect = Aspect.Full;

        // ── Step 2: retrieve (deterministic) ─────────────────────────────────
        var ctx = await _retriever.RetrieveAsync(vendorId.Value, aspect,
            filterDimension: query.FilterDimension, callerContext: null, ct);

        if (ctx is null)
            return new VendorQueryAnswer(
                "Kozmo has no record of that vendor. Please check the name and try again.",
                null);

        if (!ctx.IsAssessed)
            return new VendorQueryAnswer(
                $"Kozmo has no assessment of **{ctx.VendorName}** yet. " +
                "The vendor is known but hasn't been scored — run the pipeline with vendor data to generate an assessment.",
                ctx);

        // ── Step 3: compose (LLM phrasing, template fallback on any failure) ─
        var text = await _composer.ComposeAsync(ctx, aspect, query.RawText, ct);
        return new VendorQueryAnswer(text, ctx);
    }
}
'@
[System.IO.File]::WriteAllText('subsystems\reality-model\dotnet\Rm.Query\VendorQueryService.cs', $serviceContent)

# ── 5. VendorQueryComposer.cs — scope output when FilterDimension set ─────────
$composerContent = @'
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
/// The LLM is instructed to phrase — not generate — facts. It returns {"text": "..."}.
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

    // ── LLM prompt ───────────────────────────────────────────────────────────

    private static string BuildSystemPrompt() =>
        """
        You are Kozmo, a vendor intelligence assistant. Phrase retrieved data as a clear, concise answer.

        HARD RULES — violations are unacceptable:
        1. Every fact you state MUST come from the "Retrieved data" sections provided below. Do not add, infer, speculate, or use your own knowledge about vendors or industries.
        2. If a section has no data, say that data is not available for that category. Do not fill in plausible values.
        3. Do not re-evaluate, recompute, or second-guess the stored posture, scores, or stance.
        4. If a section shows a contradiction, report it exactly as described — do not resolve or adjudicate it.
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
                var summary = string.IsNullOrWhiteSpace(b.ReasoningSummary) ? "" : $" — {b.ReasoningSummary}";
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
                sb.AppendLine($"  • {g}");
            sb.AppendLine();
        }

        // Open check-ins (pending owner responses)
        if (ctx.OpenCheckIns.Count > 0)
        {
            sb.AppendLine($"OPEN QUESTIONS AWAITING RESPONSE ({ctx.OpenCheckIns.Count}):");
            foreach (var ci in ctx.OpenCheckIns)
                sb.AppendLine($"  • [{ci.Kind}] {ci.Question}");
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

    // ── Deterministic template fallback ──────────────────────────────────────

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
                    sb.AppendLine($"  • {g.Description}");
            }

            if (ctx.Contradictions.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine($"Contradictions ({ctx.Contradictions.Count}):");
                foreach (var c in ctx.Contradictions)
                    sb.AppendLine($"  • {c.Description} — Severity: {c.Severity}");
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
                foreach (var c in ctx.Posture.Cautions) sb.AppendLine($"  • {c}");
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
                    sb.AppendLine($"  • [Open question] {ci.Question}");
                foreach (var g in ctx.Posture.EvidenceGaps)
                    sb.AppendLine($"  • [Evidence gap] {g}");
                foreach (var g in ctx.Gaps)
                    sb.AppendLine($"  • [{g.Dimension}] {g.Description}");
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
                    sb.AppendLine($"  • [{c.Dimension}] {c.Description} — Severity: {c.Severity}");
            }
            else sb.AppendLine("Contradictions: none detected.");
        }

        return sb.ToString().TrimEnd();
    }
}
'@
[System.IO.File]::WriteAllText('subsystems\reality-model\dotnet\Rm.Query\VendorQueryComposer.cs', $composerContent)

# ── 6. VendorTools.cs — vendor_dimension_detail takes a dimension ─────────────
$vendorToolsContent = @'
using System.ComponentModel;
using Kozmo.Contracts;
using ModelContextProtocol.Server;
using Rm.Contracts;

namespace Kozmo.Mcp.Tools;

/// <summary>
/// MCP tools that expose the Reality-Model Query Service as read-only vendor intelligence tools.
/// All facts come from the deterministic retrieval layer; the LLM only phrases, never invents.
/// </summary>
[McpServerToolType]
public sealed class VendorTools
{
    private static readonly string ValidDimensions =
        "Operational, Experiential, Financial, Strategic";

    private readonly IVendorQueryService _svc;

    public VendorTools(IVendorQueryService svc) => _svc = svc;

    /// <summary>
    /// Get a complete posture overview for a named vendor: overall stance
    /// (Maintain/Monitor/Renegotiate/Escalate/Remediate), composite score band, rationale,
    /// evidence beliefs, open questions, and any detected contradictions.
    /// Use this as the starting point before drilling into a specific dimension.
    /// </summary>
    [McpServerTool(Name = "vendor_overview")]
    public async Task<string> VendorOverviewAsync(
        [Description("Name of the vendor to look up, e.g. 'Cloudwave Systems Inc.' or just 'Cloudwave'")]
        string vendorName,
        CancellationToken ct)
    {
        var answer = await _svc.AnswerAsync(new VendorQuery(vendorName, null, Aspect.Full), ct);
        return answer.Text;
    }

    /// <summary>
    /// Drill into a single business dimension for a vendor: Operational, Experiential,
    /// Financial, or Strategic. Returns that dimension's score, confidence, evidence beliefs,
    /// and any dimension-specific gaps or contradictions. Use this when the user asks about
    /// financials, operations, customer experience, or strategic factors specifically.
    /// </summary>
    [McpServerTool(Name = "vendor_dimension_detail")]
    public async Task<string> VendorDimensionDetailAsync(
        [Description("Name of the vendor to look up")]
        string vendorName,
        [Description("Which business dimension to drill into: Operational | Experiential | Financial | Strategic")]
        string dimension,
        CancellationToken ct)
    {
        var dim = dimension.Trim().ToUpperInvariant() switch
        {
            "OPERATIONAL"  => (Dimension?)Dimension.Operational,
            "EXPERIENTIAL" => Dimension.Experiential,
            "FINANCIAL"    => Dimension.Financial,
            "STRATEGIC"    => Dimension.Strategic,
            _              => null
        };

        if (dim is null)
            return $"Invalid dimension '{dimension}'. Valid values: {ValidDimensions}.";

        var query  = new VendorQuery(vendorName, null, Aspect.Full, FilterDimension: dim);
        var answer = await _svc.AnswerAsync(query, ct);
        return answer.Text;
    }

    /// <summary>
    /// List what the system is waiting to hear back on for a vendor:
    /// pending owner check-in responses, evidence gaps where no scored belief exists,
    /// and any dimensions that need more data to raise confidence.
    /// Use this to understand what information would most improve the assessment.
    /// </summary>
    [McpServerTool(Name = "vendor_open_questions")]
    public async Task<string> VendorOpenQuestionsAsync(
        [Description("Name of the vendor to look up")]
        string vendorName,
        CancellationToken ct)
    {
        var answer = await _svc.AnswerAsync(new VendorQuery(vendorName, null, Aspect.Gaps), ct);
        return answer.Text;
    }
}
'@
[System.IO.File]::WriteAllText('host\dotnet\Kozmo.Mcp\Tools\VendorTools.cs', $vendorToolsContent)

Write-Host "All source files written."
