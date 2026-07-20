using System.Globalization;
using System.Text;
using System.Text.Json;
using Kozmo.Llm;

namespace Po.VendorCall;

public interface ITranscriptComprehensionService
{
    Task<TranscriptExtractionResult> ExtractAsync(
        TranscriptComprehensionContext context,
        CancellationToken ct);
}

/// <summary>Input to the comprehension service.</summary>
public sealed record TranscriptComprehensionContext(
    VendorCallRun                 Run,
    TranscriptParseResult         ParsedTranscript,
    VendorCallBriefing?           PreMeetingBriefing,
    IReadOnlyList<string>         OpenItemsFromBrief);

/// <summary>
/// LLM-powered extraction of commercial items from parsed transcript segments.
///
/// Uses IKozmoLlm.CompleteJsonAsync with a {"items":[...]} JSON object response.
/// If the LLM is null, unavailable, or returns unparseable output, returns an empty
/// result (never crashes).
///
/// Confidence tiers:
///   >= 0.85 — high (RequiresUserConfirmation = false)
///   0.55–0.84 — medium (RequiresUserConfirmation = true)
///   < 0.55 — discarded
///
/// Grounding check: every item whose quote cannot be found (case-insensitive substring)
/// in the transcript text is discarded and counted in DiscardedLowConfidenceCount.
/// </summary>
public sealed class TranscriptComprehensionService : ITranscriptComprehensionService
{
    private const int    WordsThreshold = 3000;
    private const int    ChunkWords     = 2500;
    private const int    OverlapWords   = 200;
    private const int    MaxTokens      = 2000;
    private const double HighThreshold  = 0.85;
    private const double MedThreshold   = 0.55;

    private readonly IKozmoLlm? _llm;

    public TranscriptComprehensionService(IKozmoLlm? llm) => _llm = llm;

    public async Task<TranscriptExtractionResult> ExtractAsync(
        TranscriptComprehensionContext context,
        CancellationToken ct)
    {
        var started  = DateTimeOffset.UtcNow;
        var segments = context.ParsedTranscript.Segments;

        if (_llm is null || segments.Count == 0)
            return EmptyResult(segments.Count, DateTimeOffset.UtcNow - started);

        // ── Extract (single pass or chunked) ─────────────────────────────────
        List<TranscriptExtractedItem> rawItems;

        if (context.ParsedTranscript.TotalWordCount > WordsThreshold)
        {
            var chunks        = ChunkSegments(segments);
            var allChunkItems = new List<TranscriptExtractedItem>();

            foreach (var chunk in chunks)
            {
                var chunkItems = await CallAndParseAsync(context, chunk, ct);
                allChunkItems.AddRange(chunkItems);
            }

            rawItems = DeduplicateByQuote(allChunkItems);
        }
        else
        {
            rawItems = await CallAndParseAsync(context, segments, ct);
        }

        // ── Grounding check ───────────────────────────────────────────────────
        var fullText        = string.Join(" ", segments.Select(s => s.Text));
        var groundingFails  = 0;
        var groundedRaw     = new List<TranscriptExtractedItem>();

        foreach (var item in rawItems)
        {
            if (IsGrounded(item.Quote, fullText))
                groundedRaw.Add(item);
            else
                groundingFails++;
        }

        // ── Confidence thresholds ─────────────────────────────────────────────
        var highConf = groundedRaw.Where(i => i.Confidence >= HighThreshold).ToList();
        var medConf  = groundedRaw.Where(i => i.Confidence >= MedThreshold && i.Confidence < HighThreshold).ToList();
        var lowConf  = groundedRaw.Where(i => i.Confidence <  MedThreshold).ToList();

        var finalItems = highConf.Select(i => i with { RequiresUserConfirmation = false })
            .Concat(medConf.Select(i => i with { RequiresUserConfirmation = true }))
            .ToList();

        // ── Cross-reference against pre-brief open items ──────────────────────
        var resolutions = CrossReferenceOpenItems(context.OpenItemsFromBrief, finalItems);

        var metadata = new TranscriptExtractionMetadata(
            TotalSegmentsProcessed:      segments.Count,
            TotalItemsExtracted:         finalItems.Count,
            HighConfidenceCount:         highConf.Count,
            RequiresConfirmationCount:   medConf.Count,
            DiscardedLowConfidenceCount: lowConf.Count + groundingFails,
            ProcessingDuration:          DateTimeOffset.UtcNow - started);

        return new TranscriptExtractionResult(finalItems, resolutions, metadata);
    }

    // ── Chunking ──────────────────────────────────────────────────────────────

    /// <summary>Splits segments into overlapping chunks so each chunk fits within ChunkWords.</summary>
    public static List<List<TranscriptSegment>> ChunkSegments(
        IReadOnlyList<TranscriptSegment> segments)
    {
        var chunks  = new List<List<TranscriptSegment>>();
        var current = new List<TranscriptSegment>();
        var words   = 0;

        for (var i = 0; i < segments.Count; i++)
        {
            var seg     = segments[i];
            var segWords = CountWords(seg.Text);
            current.Add(seg);
            words += segWords;

            if (words >= ChunkWords && i < segments.Count - 1)
            {
                chunks.Add([.. current]);

                // Walk back ~OverlapWords for the next chunk's prefix
                var overlap      = new List<TranscriptSegment>();
                var overlapCount = 0;
                for (var j = current.Count - 1; j >= 0 && overlapCount < OverlapWords; j--)
                {
                    overlap.Insert(0, current[j]);
                    overlapCount += CountWords(current[j].Text);
                }

                current = overlap;
                words   = overlapCount;
            }
        }

        if (current.Count > 0)
            chunks.Add(current);

        return chunks;
    }

    // ── LLM call + parsing ────────────────────────────────────────────────────

    private async Task<List<TranscriptExtractedItem>> CallAndParseAsync(
        TranscriptComprehensionContext context,
        IReadOnlyList<TranscriptSegment> segments,
        CancellationToken ct)
    {
        var system = BuildSystemPrompt(context.Run.VendorName);
        var user   = BuildUserPrompt(context, segments);

        var items = await TryCallAndParseAsync(system, user, ct);
        if (items is not null) return items;

        // Retry once with explicit format reminder
        var retryUser = user + "\n\nIMPORTANT: Return ONLY a JSON object {\"items\": [...]}. No preamble, no markdown.";
        return await TryCallAndParseAsync(system, retryUser, ct) ?? [];
    }

    private async Task<List<TranscriptExtractedItem>?> TryCallAndParseAsync(
        string system, string user, CancellationToken ct)
    {
        try
        {
            var result = await _llm!.CompleteJsonAsync(system, user, MaxTokens, ct);
            return TryParseItems(result);
        }
        catch
        {
            return null;
        }
    }

    internal static List<TranscriptExtractedItem>? TryParseItems(LlmResult result)
    {
        try
        {
            if (result.Answer is not JsonElement el) return null;

            // Prefer {"items": [...]} (required by JSON object mode in OpenAI)
            if (el.ValueKind == JsonValueKind.Object &&
                el.TryGetProperty("items", out var arrProp) &&
                arrProp.ValueKind == JsonValueKind.Array)
                return ParseArray(arrProp);

            // Also accept a bare array (for test stubs / future providers)
            if (el.ValueKind == JsonValueKind.Array)
                return ParseArray(el);

            return null;
        }
        catch
        {
            return null;
        }
    }

    private static List<TranscriptExtractedItem> ParseArray(JsonElement arr)
    {
        var items = new List<TranscriptExtractedItem>();
        foreach (var el in arr.EnumerateArray())
        {
            var item = TryParseItem(el);
            if (item is not null) items.Add(item);
        }
        return items;
    }

    private static TranscriptExtractedItem? TryParseItem(JsonElement el)
    {
        try
        {
            var typeStr = el.TryGetProperty("type",         out var tp) ? tp.GetString() : null;
            var type    = ParseItemType(typeStr);
            if (type is null) return null;

            var description = el.TryGetProperty("description",  out var dp) ? dp.GetString() ?? "" : "";
            var speaker     = el.TryGetProperty("speaker",      out var sp) ? sp.GetString() ?? "Unknown" : "Unknown";
            var counterParty = el.TryGetProperty("counterParty", out var cp) &&
                               cp.ValueKind != JsonValueKind.Null ? cp.GetString() : null;
            var quote       = el.TryGetProperty("quote",        out var qp) ? qp.GetString() ?? "" : "";
            var tsStr       = el.TryGetProperty("timestamp",    out var tsp) ? tsp.GetString() : null;
            var confidence  = el.TryGetProperty("confidence",   out var cfp) ? cfp.GetDouble() : 0.0;
            var owner       = el.TryGetProperty("owner",        out var op) &&
                              op.ValueKind != JsonValueKind.Null ? op.GetString() : null;
            var dueDate     = el.TryGetProperty("dueDate",      out var ddp) &&
                              ddp.ValueKind != JsonValueKind.Null ? ddp.GetString() : null;

            return new TranscriptExtractedItem(
                Type:                     type.Value,
                Description:              description,
                Speaker:                  speaker,
                CounterParty:             counterParty,
                Quote:                    quote,
                TranscriptTimestamp:      ParseTimestamp(tsStr),
                Confidence:               confidence,
                ClaimKey:                 MapClaimKey(type.Value),
                Owner:                    owner,
                DueDate:                  dueDate,
                RequiresUserConfirmation: false); // set by caller after threshold check
        }
        catch
        {
            return null;
        }
    }

    // ── Deduplication (for chunked extraction) ────────────────────────────────

    private static List<TranscriptExtractedItem> DeduplicateByQuote(
        List<TranscriptExtractedItem> items)
    {
        var result = new List<TranscriptExtractedItem>();
        foreach (var item in items)
        {
            var isDup = result.Any(existing => QuoteSimilarity(existing.Quote, item.Quote) > 0.7);
            if (!isDup) result.Add(item);
        }
        return result;
    }

    private static double QuoteSimilarity(string a, string b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return 0;
        var aL = a.ToLowerInvariant();
        var bL = b.ToLowerInvariant();
        if (aL.Contains(bL) || bL.Contains(aL)) return 1.0;
        var prefix = aL.Length > 20 ? aL[..20] : aL;
        return bL.Contains(prefix) ? 0.8 : 0.0;
    }

    // ── Grounding check ───────────────────────────────────────────────────────

    internal static bool IsGrounded(string quote, string fullTranscriptText)
    {
        if (string.IsNullOrWhiteSpace(quote)) return false;
        var q = quote.Trim().ToLowerInvariant();
        var t = fullTranscriptText.ToLowerInvariant();

        if (t.Contains(q)) return true;

        // Allow for slight rewording: check first 25 characters
        if (q.Length >= 25 && t.Contains(q[..25])) return true;

        return false;
    }

    // ── Cross-reference against pre-brief open items ──────────────────────────

    private static IReadOnlyList<PreBriefItemResolution> CrossReferenceOpenItems(
        IReadOnlyList<string>          openItems,
        IReadOnlyList<TranscriptExtractedItem> extractedItems)
    {
        var resolutions = new List<PreBriefItemResolution>();

        foreach (var openItem in openItems)
        {
            var keywords = openItem.ToLowerInvariant()
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Where(w => w.Length >= 3)
                .ToList();

            if (keywords.Count == 0)
            {
                resolutions.Add(new PreBriefItemResolution(openItem, false, null, null, 0));
                continue;
            }

            TranscriptExtractedItem? bestMatch = null;
            double bestScore = 0;

            foreach (var item in extractedItems)
            {
                var text       = (item.Description + " " + item.Quote).ToLowerInvariant();
                var matchCount = keywords.Count(kw => text.Contains(kw));
                var score      = (double)matchCount / keywords.Count * item.Confidence;
                if (score > bestScore)
                {
                    bestScore = score;
                    bestMatch = item;
                }
            }

            resolutions.Add(bestScore >= 0.3 && bestMatch is not null
                ? new PreBriefItemResolution(
                    PreBriefItem:        openItem,
                    AddressedInMeeting:  true,
                    TranscriptEvidence:  bestMatch.Description,
                    TranscriptTimestamp: bestMatch.TranscriptTimestamp,
                    Confidence:          bestScore)
                : new PreBriefItemResolution(openItem, false, null, null, bestScore));
        }

        return resolutions;
    }

    // ── Prompt builders ───────────────────────────────────────────────────────

    private static string BuildSystemPrompt(string vendorName) =>
        $$"""
        You are extracting commercial information from a vendor meeting transcript involving {{vendorName}}.

        Extract ONLY items that were explicitly stated in the transcript.
        Do NOT infer, assume, or add anything not said.

        Return a JSON object in this format:
        {"items": [
          {
            "type": one of "decision" | "commitment" | "pricing_signal" | "service_signal" | "open_question" | "next_step",
            "description": "what was said or agreed (one sentence)",
            "speaker": "exact speaker name from transcript",
            "counterParty": "who it was directed at, or null",
            "quote": "exact words from transcript, max 2 sentences",
            "timestamp": "HH:MM:SS from transcript",
            "confidence": 0.0 to 1.0,
            "owner": "who is responsible, or null",
            "dueDate": "when it is due if mentioned, else null"
          }
        ]}

        Confidence guidance:
        - "I will send it by Friday" = 0.90+ (clear commitment)
        - "I'll try to get that done" = 0.65 (hedged)
        - "Should we consider..." = 0.40 (hypothetical, not a decision)
        - Questions being asked but not answered = open_question with confidence ~0.75

        Return ONLY the JSON object. No preamble, no markdown fences.
        """;

    private static string BuildUserPrompt(
        TranscriptComprehensionContext   context,
        IReadOnlyList<TranscriptSegment> segments)
    {
        var run = context.Run;
        var sb  = new StringBuilder();

        sb.AppendLine($"Meeting: {run.MeetingSubject}");
        sb.AppendLine($"Vendor: {run.VendorName}");
        sb.AppendLine($"Date: {run.StartUtc:yyyy-MM-dd}");

        if (context.OpenItemsFromBrief.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Open items from pre-meeting brief (check if addressed):");
            foreach (var item in context.OpenItemsFromBrief)
                sb.AppendLine($"- {item}");
        }

        sb.AppendLine();
        sb.AppendLine("--- TRANSCRIPT ---");
        foreach (var seg in segments)
            sb.AppendLine($"[{FormatTimestamp(seg.StartTime)}] {seg.Speaker}: \"{seg.Text}\"");

        return sb.ToString();
    }

    // ── Mapping helpers ───────────────────────────────────────────────────────

    private static TranscriptItemType? ParseItemType(string? s) => s?.ToLowerInvariant() switch
    {
        "decision"       => TranscriptItemType.Decision,
        "commitment"     => TranscriptItemType.Commitment,
        "pricing_signal" => TranscriptItemType.PricingSignal,
        "service_signal" => TranscriptItemType.ServiceSignal,
        "open_question"  => TranscriptItemType.OpenQuestion,
        "next_step"      => TranscriptItemType.NextStep,
        _                => null
    };

    private static string MapClaimKey(TranscriptItemType type) => type switch
    {
        TranscriptItemType.Decision      => "meeting.vendor_call.decision",
        TranscriptItemType.Commitment    => "vendor.commitment.description",
        TranscriptItemType.PricingSignal => "vendor.communication.pricing_signal",
        TranscriptItemType.ServiceSignal => "vendor.communication.service_signal",
        TranscriptItemType.OpenQuestion  => "vendor.evidence_gap",
        TranscriptItemType.NextStep      => "vendor.commitment.description",
        _                                => "vendor.communication.general"
    };

    private static TimeSpan ParseTimestamp(string? s)
    {
        if (string.IsNullOrEmpty(s)) return TimeSpan.Zero;
        if (TimeSpan.TryParseExact(s, @"hh\:mm\:ss", CultureInfo.InvariantCulture, out var ts))
            return ts;
        return TimeSpan.Zero;
    }

    private static string FormatTimestamp(TimeSpan ts) =>
        $"{(int)ts.TotalHours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2}";

    private static int CountWords(string text) =>
        string.IsNullOrWhiteSpace(text)
            ? 0
            : text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;

    private static TranscriptExtractionResult EmptyResult(int segCount, TimeSpan duration) =>
        new(
            Items:                 [],
            ResolvedPreBriefItems: [],
            Metadata:              new TranscriptExtractionMetadata(
                segCount, 0, 0, 0, 0, duration));
}
