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
///   1. IntentParser  â€” parse raw text â†’ (vendorId?, aspect)
///   2. VendorQueryRetriever â€” deterministic read-model pull â†’ RetrievedContext
///   3. VendorQueryComposer  â€” LLM phrasing (or template fallback) â†’ prose text
///
/// Invariants enforced here:
///   - Unknown vendor â†’ honest "no such vendor" answer; compose is NOT called.
///   - Resolved but not assessed â†’ honest "no assessment yet"; compose is NOT called.
///   - Ambiguous vendor name â†’ list candidates; compose is NOT called.
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

        // â”€â”€ Step 1: parse (skip if caller pre-resolved the vendor) â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
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

        // â”€â”€ Step 2: retrieve (deterministic) â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        var ctx = await _retriever.RetrieveAsync(vendorId.Value, aspect,
            filterDimension: query.FilterDimension, callerContext: null, ct);

        if (ctx is null)
            return new VendorQueryAnswer(
                "Kozmo has no record of that vendor. Please check the name and try again.",
                null);

        if (!ctx.IsAssessed)
            return new VendorQueryAnswer(
                $"Kozmo has no assessment of **{ctx.VendorName}** yet. " +
                "The vendor is known but hasn't been scored â€” run the pipeline with vendor data to generate an assessment.",
                ctx);

        // â”€â”€ Step 3: compose (LLM phrasing, template fallback on any failure) â”€
        var text = await _composer.ComposeAsync(ctx, aspect, query.RawText, ct);
        return new VendorQueryAnswer(text, ctx);
    }
}