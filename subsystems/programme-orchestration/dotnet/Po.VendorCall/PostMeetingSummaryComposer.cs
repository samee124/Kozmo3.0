namespace Po.VendorCall;

public interface IPostMeetingSummaryComposer
{
    PostMeetingSummary Compose(PostMeetingSummaryContext context);
}

/// <summary>Everything PostMeetingSummaryComposer needs to produce a post-meeting summary.</summary>
public sealed record PostMeetingSummaryContext(
    VendorCallRun              Run,
    TranscriptExtractionResult Extraction,
    VendorCallBriefing?        PreMeetingBriefing,
    VendorCallEvidenceBundle?  EvidenceBundle);

/// <summary>
/// Composes a structured post-meeting summary from a transcript extraction result.
///
/// Fully deterministic — no LLM. Same extraction input always produces byte-identical output.
/// The PostMeetingEmailComposer handles optional LLM rephrase of the final email.
/// </summary>
[Obsolete("Superseded by ReviewComposer + ReviewEmailRenderer — kept for reference, not called from the active pipeline")]
public sealed class PostMeetingSummaryComposer : IPostMeetingSummaryComposer
{
    internal const string PreKey = "pre-meeting-brief";

    public PostMeetingSummary Compose(PostMeetingSummaryContext context)
    {
        var run        = context.Run;
        var extraction = context.Extraction;
        var resolved   = extraction.ResolvedPreBriefItems;
        var items      = extraction.Items;

        // ── Build citation registry ───────────────────────────────────────────
        var citations = new List<SummaryCitation>();
        var citIdx    = 0;
        var tsCiteMap = new Dictionary<string, SummaryCitation>(StringComparer.OrdinalIgnoreCase);

        var preCite = new SummaryCitation(
            Index:               ++citIdx,
            SourceDescription:   $"Pre-meeting brief: {run.VendorName}",
            TranscriptTimestamp: null,
            SourceDate:          run.StartUtc);
        citations.Add(preCite);

        // One citation per unique transcript timestamp across all items
        foreach (var item in items)
        {
            var tsKey = FormatTs(item.TranscriptTimestamp);
            if (!tsCiteMap.ContainsKey(tsKey))
            {
                var desc = item.Description.Length > 50
                    ? item.Description[..50] + "…"
                    : item.Description;
                var cite = new SummaryCitation(++citIdx, $"Transcript: {desc}", tsKey, null);
                tsCiteMap[tsKey] = cite;
                citations.Add(cite);
            }
        }
        // Also register timestamps from resolved pre-brief items
        foreach (var r in resolved)
        {
            if (!r.TranscriptTimestamp.HasValue) continue;
            var tsKey = FormatTs(r.TranscriptTimestamp.Value);
            if (!tsCiteMap.ContainsKey(tsKey))
            {
                var cite = new SummaryCitation(++citIdx, $"Transcript: {r.PreBriefItem}", tsKey, null);
                tsCiteMap[tsKey] = cite;
                citations.Add(cite);
            }
        }

        return new PostMeetingSummary(
            VendorName:            run.VendorName,
            MeetingTime:           run.StartUtc,
            MeetingSubject:        run.MeetingSubject,
            Attendees:             [],
            MeetingOutcome:        ComposeMeetingOutcome(resolved, tsCiteMap),
            DecisionsMade:         ComposeDecisionsMade(items, tsCiteMap),
            NewCommitments:        ComposeNewCommitments(items, run, tsCiteMap),
            ResolvedFromPreBrief:  ComposeResolvedFromPreBrief(resolved, tsCiteMap),
            CommercialStateChange: ComposeCommercialStateChange(resolved, items),
            StillOpen:             ComposeStillOpen(resolved, items, tsCiteMap),
            RecommendedNextAction: ComposeRecommendedNextAction(resolved, items, tsCiteMap),
            Citations:             citations);
    }

    // ── Section composers ─────────────────────────────────────────────────────

    private static SummarySection ComposeMeetingOutcome(
        IReadOnlyList<PreBriefItemResolution> resolved,
        Dictionary<string, SummaryCitation>   tsCiteMap)
    {
        var addressedCount = resolved.Count(r => r.AddressedInMeeting);
        var totalCount     = resolved.Count;

        string content;
        if (totalCount == 0)
            content = "No pre-meeting items were tracked for this meeting.";
        else
        {
            var assessment = addressedCount > totalCount / 2
                ? "The meeting made progress on the key issues."
                : "Several key items were not addressed and remain open.";
            content = $"{addressedCount} of {totalCount} pre-meeting items were addressed. {assessment}";
        }

        var srcs = new List<string> { PreKey };
        foreach (var r in resolved.Where(r => r.AddressedInMeeting && r.TranscriptTimestamp.HasValue))
        {
            var tsKey = FormatTs(r.TranscriptTimestamp!.Value);
            if (tsCiteMap.ContainsKey(tsKey) && !srcs.Contains(tsKey))
                srcs.Add(tsKey);
        }

        return new SummarySection("MEETING OUTCOME", content, [], srcs);
    }

    private static SummarySection ComposeDecisionsMade(
        IReadOnlyList<TranscriptExtractedItem> items,
        Dictionary<string, SummaryCitation>    tsCiteMap)
    {
        var decisions  = items.Where(i => i.Type == TranscriptItemType.Decision).ToList();
        var lineItems  = new List<SummaryLineItem>();
        var srcs       = new List<string>();

        string content = decisions.Count == 0
            ? "No explicit decisions were recorded in this meeting."
            : $"{decisions.Count} decision(s) recorded.";

        foreach (var d in decisions)
        {
            var tsKey = FormatTs(d.TranscriptTimestamp);
            lineItems.Add(new SummaryLineItem(
                Text:                     d.Description,
                Speaker:                  d.Speaker,
                Owner:                    d.Owner,
                DueDate:                  d.DueDate,
                TranscriptTimestamp:      tsKey,
                Confidence:               d.Confidence,
                RequiresUserConfirmation: d.RequiresUserConfirmation,
                SourceReference:          tsKey));
            if (!srcs.Contains(tsKey)) srcs.Add(tsKey);
        }

        if (srcs.Count == 0) srcs.Add(PreKey);
        return new SummarySection("DECISIONS MADE", content, lineItems, srcs);
    }

    private static SummarySection ComposeNewCommitments(
        IReadOnlyList<TranscriptExtractedItem> items,
        VendorCallRun                          run,
        Dictionary<string, SummaryCitation>    tsCiteMap)
    {
        var commits   = items
            .Where(i => i.Type is TranscriptItemType.Commitment or TranscriptItemType.NextStep)
            .ToList();
        var lineItems = new List<SummaryLineItem>();
        var srcs      = new List<string>();
        var vendor    = 0;
        var internal_ = 0;

        foreach (var c in commits)
        {
            var tsKey      = FormatTs(c.TranscriptTimestamp);
            var isInternal = IsInternalSpeaker(c.Speaker, run.SignedInUserPrincipalId);
            if (isInternal) internal_++; else vendor++;

            var prefix = isInternal ? "[You] " : "[Vendor] ";
            lineItems.Add(new SummaryLineItem(
                Text:                     prefix + c.Description,
                Speaker:                  c.Speaker,
                Owner:                    c.Owner,
                DueDate:                  c.DueDate,
                TranscriptTimestamp:      tsKey,
                Confidence:               c.Confidence,
                RequiresUserConfirmation: c.RequiresUserConfirmation,
                SourceReference:          tsKey));
            if (!srcs.Contains(tsKey)) srcs.Add(tsKey);
        }

        string content = commits.Count == 0
            ? "No new commitments were made in this meeting."
            : $"Vendor commitments: {vendor}. Your commitments: {internal_}.";

        if (srcs.Count == 0) srcs.Add(PreKey);
        return new SummarySection("NEW COMMITMENTS", content, lineItems, srcs);
    }

    private static SummarySection ComposeResolvedFromPreBrief(
        IReadOnlyList<PreBriefItemResolution> resolved,
        Dictionary<string, SummaryCitation>   tsCiteMap)
    {
        var lineItems = new List<SummaryLineItem>();
        var srcs      = new List<string> { PreKey };

        foreach (var r in resolved)
        {
            string text;
            string? tsKey = null;

            if (r.AddressedInMeeting && r.TranscriptTimestamp.HasValue)
            {
                tsKey = FormatTs(r.TranscriptTimestamp.Value);
                var evidence = r.TranscriptEvidence ?? "addressed in meeting";
                text = $"✓ {r.PreBriefItem} — {evidence}";
                if (!srcs.Contains(tsKey)) srcs.Add(tsKey);
            }
            else if (r.AddressedInMeeting)
            {
                text = $"? {r.PreBriefItem} — possibly addressed (requires confirmation)";
            }
            else
            {
                text = $"✗ {r.PreBriefItem} — not addressed in this meeting";
            }

            lineItems.Add(new SummaryLineItem(
                Text:                     text,
                Speaker:                  null,
                Owner:                    null,
                DueDate:                  null,
                TranscriptTimestamp:      tsKey,
                Confidence:               r.Confidence,
                RequiresUserConfirmation: false,
                SourceReference:          tsKey ?? PreKey));
        }

        string content = resolved.Count == 0
            ? "No pre-meeting items were tracked."
            : $"{resolved.Count(r => r.AddressedInMeeting)} of {resolved.Count} pre-meeting items addressed.";

        return new SummarySection("RESOLVED FROM PRE-MEETING BRIEF", content, lineItems, srcs);
    }

    private static SummarySection ComposeCommercialStateChange(
        IReadOnlyList<PreBriefItemResolution>  resolved,
        IReadOnlyList<TranscriptExtractedItem> items)
    {
        var before         = resolved.Count;
        var addressedCount = resolved.Count(r => r.AddressedInMeeting);
        var stillOpenCount = resolved.Count(r => !r.AddressedInMeeting)
                           + items.Count(i => i.Type == TranscriptItemType.OpenQuestion);
        var newCommits     = items.Count(i =>
            i.Type is TranscriptItemType.Commitment or TranscriptItemType.NextStep);

        var assessment = addressedCount > stillOpenCount ? "Improving"
            : addressedCount < stillOpenCount           ? "Needs attention"
            :                                             "Stable";

        string content;
        if (before == 0)
            content = $"No pre-meeting pressure points tracked. {newCommits} new commitment(s) made. Assessment: {assessment}.";
        else
            content = $"Before: {before} pressure point{(before == 1 ? "" : "s")} from the pre-meeting brief. " +
                      $"After: {addressedCount} addressed, {newCommits} new commitment{(newCommits == 1 ? "" : "s")} made, " +
                      $"{stillOpenCount} still open. Assessment: {assessment}.";

        return new SummarySection("COMMERCIAL STATE CHANGE", content, [], [PreKey]);
    }

    private static SummarySection ComposeStillOpen(
        IReadOnlyList<PreBriefItemResolution>  resolved,
        IReadOnlyList<TranscriptExtractedItem> items,
        Dictionary<string, SummaryCitation>    tsCiteMap)
    {
        var lineItems = new List<SummaryLineItem>();
        var srcs      = new List<string> { PreKey };

        // Unresolved pre-brief items
        foreach (var r in resolved.Where(r => !r.AddressedInMeeting))
        {
            lineItems.Add(new SummaryLineItem(
                Text:                     r.PreBriefItem,
                Speaker:                  null,
                Owner:                    null,
                DueDate:                  null,
                TranscriptTimestamp:      null,
                Confidence:               r.Confidence,
                RequiresUserConfirmation: false,
                SourceReference:          PreKey));
        }

        // New open questions surfaced in transcript
        foreach (var q in items.Where(i => i.Type == TranscriptItemType.OpenQuestion))
        {
            var tsKey = FormatTs(q.TranscriptTimestamp);
            lineItems.Add(new SummaryLineItem(
                Text:                     q.Description,
                Speaker:                  q.Speaker,
                Owner:                    q.Owner,
                DueDate:                  null,
                TranscriptTimestamp:      tsKey,
                Confidence:               q.Confidence,
                RequiresUserConfirmation: q.RequiresUserConfirmation,
                SourceReference:          tsKey));
            if (!srcs.Contains(tsKey)) srcs.Add(tsKey);
        }

        // Commitments needing confirmation
        foreach (var c in items.Where(i =>
            i.Type is TranscriptItemType.Commitment or TranscriptItemType.NextStep
            && i.RequiresUserConfirmation))
        {
            var tsKey = FormatTs(c.TranscriptTimestamp);
            lineItems.Add(new SummaryLineItem(
                Text:                     $"{c.Description} (requires confirmation)",
                Speaker:                  c.Speaker,
                Owner:                    c.Owner,
                DueDate:                  c.DueDate,
                TranscriptTimestamp:      tsKey,
                Confidence:               c.Confidence,
                RequiresUserConfirmation: true,
                SourceReference:          tsKey));
            if (!srcs.Contains(tsKey)) srcs.Add(tsKey);
        }

        string content = lineItems.Count == 0
            ? "All tracked items were addressed."
            : $"{lineItems.Count} item(s) remain open or require attention.";

        return new SummarySection("STILL OPEN", content, lineItems, srcs);
    }

    private static SummarySection ComposeRecommendedNextAction(
        IReadOnlyList<PreBriefItemResolution>  resolved,
        IReadOnlyList<TranscriptExtractedItem> items,
        Dictionary<string, SummaryCitation>    tsCiteMap)
    {
        // Priority 1: nearest-deadline commitment with a day-of-week or short due-date
        var nearTerm = items
            .Where(i => i.Type is TranscriptItemType.Commitment or TranscriptItemType.NextStep
                     && i.DueDate != null
                     && IsNearTermDueDate(i.DueDate!))
            .OrderBy(i => i.TranscriptTimestamp)
            .FirstOrDefault();

        if (nearTerm != null)
        {
            var tsKey   = FormatTs(nearTerm.TranscriptTimestamp);
            var who     = nearTerm.Owner ?? nearTerm.Speaker;
            var content = $"Follow up on {who}'s commitment: \"{nearTerm.Description}\" — due {nearTerm.DueDate}.";
            return new SummarySection("RECOMMENDED NEXT ACTION", content,
                [new SummaryLineItem(content, nearTerm.Speaker, nearTerm.Owner,
                    nearTerm.DueDate, tsKey, nearTerm.Confidence, false, tsKey)],
                [tsKey]);
        }

        // Priority 2: unresolved pre-brief items
        var unresolved = resolved.Where(r => !r.AddressedInMeeting).ToList();
        if (unresolved.Count > 0)
        {
            var names   = string.Join(", ", unresolved.Take(2).Select(r => r.PreBriefItem));
            var content = $"Schedule a follow-up to address: {names}.";
            return new SummarySection("RECOMMENDED NEXT ACTION", content,
                [new SummaryLineItem(content, null, null, null, null, 0.8, false, PreKey)],
                [PreKey]);
        }

        // Default
        return new SummarySection("RECOMMENDED NEXT ACTION",
            "Monitor new commitments and follow up as deadlines approach.",
            [], [PreKey]);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static readonly string[] DayNames =
        ["Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday",
         "Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun"];

    private static bool IsNearTermDueDate(string dueDate) =>
        DayNames.Any(d => dueDate.Contains(d, StringComparison.OrdinalIgnoreCase));

    internal static bool IsInternalSpeaker(string speaker, string principalId)
    {
        if (!principalId.Contains('@')) return false;
        var prefix = principalId.Split('@')[0];
        return speaker.Contains(prefix, StringComparison.OrdinalIgnoreCase);
    }

    internal static string FormatTs(TimeSpan ts) =>
        $"{(int)ts.TotalMinutes:D2}:{ts.Seconds:D2}";
}
