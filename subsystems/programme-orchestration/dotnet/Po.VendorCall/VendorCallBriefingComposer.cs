using System.Globalization;
using System.Text;
using System.Text.Json;
using Kozmo.Contracts;
using Kozmo.Llm;
using If.Contracts;

namespace Po.VendorCall;

/// <summary>
/// Composes a structured pre-meeting brief from a VendorCallBriefingContext.
///
/// Mode A (default): fully deterministic template composition — always works,
/// no LLM required.
///
/// Mode B (optional): if an IKozmoLlm is provided, each section's prose is
/// rephrased for readability. LLM failures degrade silently to Mode A.
/// The LLM ONLY rephrases — it never adds or removes facts or citations.
/// </summary>
[Obsolete("Superseded by ReviewComposer + ReviewEmailRenderer — kept for reference, not called from the active pipeline")]
public sealed class VendorCallBriefingComposer
{
    private readonly IKozmoLlm? _llm;

    /// <param name="llm">Optional LLM for Mode B rephrase. Null = Mode A only.</param>
    public VendorCallBriefingComposer(IKozmoLlm? llm = null) => _llm = llm;

    public async Task<VendorCallBriefing> ComposeAsync(
        VendorCallBriefingContext ctx,
        CancellationToken         ct = default)
    {
        var citations = new CitationBuilder();

        var meetingObjective    = ComposeMeetingObjective(ctx, citations);
        var contractPosition    = ComposeContractPosition(ctx, citations);
        var recentDevelopments  = ComposeRecentDevelopments(ctx, citations);
        var openCommitments     = ComposeOpenCommitments(ctx, citations);
        var risksOpps           = ComposeRisksAndOpportunities(ctx, citations);
        var evidenceGaps        = ComposeEvidenceGaps(ctx, citations);
        var recommendedQs       = ComposeRecommendedQuestions(ctx, citations);
        var safestNextAction    = ComposeSafestNextAction(ctx, citations);

        // Mode B: try to rephrase sections if LLM is available
        if (_llm is not null)
        {
            meetingObjective   = await TryRephrase(meetingObjective,   _llm, ct);
            contractPosition   = await TryRephrase(contractPosition,   _llm, ct);
            recentDevelopments = await TryRephrase(recentDevelopments, _llm, ct);
            openCommitments    = await TryRephrase(openCommitments,    _llm, ct);
            risksOpps          = await TryRephrase(risksOpps,          _llm, ct);
            recommendedQs      = await TryRephrase(recommendedQs,      _llm, ct);
            safestNextAction   = await TryRephrase(safestNextAction,   _llm, ct);
        }

        return new VendorCallBriefing(
            VendorName:           ctx.VendorName,
            MeetingTime:          ctx.Meeting.StartUtc,
            MeetingSubject:       ctx.Meeting.Subject,
            Attendees:            ctx.Meeting.Attendees,
            MeetingObjective:     meetingObjective,
            ContractPosition:     contractPosition,
            RecentDevelopments:   recentDevelopments,
            OpenCommitments:      openCommitments,
            RisksAndOpportunities: risksOpps,
            EvidenceGaps:         evidenceGaps,
            RecommendedQuestions: recommendedQs,
            SafestNextAction:     safestNextAction,
            Citations:            citations.Build());
    }

    // ── Section composers (Mode A deterministic) ──────────────────────────────

    private static BriefingSection ComposeMeetingObjective(
        VendorCallBriefingContext ctx, CitationBuilder citations)
    {
        var meetingCiteId = citations.Register(
            sourceId:    ctx.Meeting.ExternalId,
            description: $"Calendar event: {ctx.Meeting.Subject}",
            date:        ctx.Meeting.StartUtc);

        var sb = new StringBuilder();
        sb.Append($"Vendor meeting: {ctx.VendorName} — {ctx.Meeting.Subject}.");

        // List recipe sections that have non-empty evidence
        var topicParts = new List<string>();
        if (ctx.Contracts.Count > 0)          topicParts.Add("contract position");
        if (ctx.RecentEmails.Count > 0)       topicParts.Add("recent communications");
        if (ctx.PriorMeetingNotes.Count > 0)  topicParts.Add("prior meeting outcomes");
        if (ctx.OpenCommitments.Count > 0)    topicParts.Add("open commitments");
        if (ctx.CommercialSignals.Count > 0)  topicParts.Add("commercial signals");
        if (ctx.EvidenceGaps.Count > 0)       topicParts.Add("evidence gaps");

        if (topicParts.Count > 0)
            sb.Append($" Key topics: {string.Join(", ", topicParts)}.");

        var attendeeCount = ctx.Meeting.Attendees.Count;
        if (attendeeCount > 0)
            sb.Append($" {attendeeCount} attendee(s).");

        return new BriefingSection(
            Heading:          "MEETING OBJECTIVE",
            Content:          sb.ToString(),
            SourceReferences: [meetingCiteId]);
    }

    private static BriefingSection ComposeContractPosition(
        VendorCallBriefingContext ctx, CitationBuilder citations)
    {
        if (ctx.Contracts.Count == 0)
        {
            var meetingCiteId = citations.Register(
                ctx.Meeting.ExternalId,
                $"Calendar event: {ctx.Meeting.Subject}",
                ctx.Meeting.StartUtc);

            return new BriefingSection(
                Heading:          "CONTRACT POSITION",
                Content:          $"No signed contract on file for {ctx.VendorName}.",
                SourceReferences: [meetingCiteId]);
        }

        // Current contract = most recently ingested
        var current = ctx.Contracts.OrderByDescending(e => e.IngestedAt).First();
        var citeId  = citations.Register(
            sourceId:    current.EvidenceId.ToString(),
            description: $"Signed contract: {Path.GetFileName(current.Ref)}",
            date:        current.IngestedAt);

        // Extract structural claim values from beliefs
        var annualValue   = FindBelief(ctx.CurrentBeliefs, "annual_value")?.Value;
        var renewalDateRaw= FindBelief(ctx.CurrentBeliefs, "renewal_date")?.Value;
        var noticePeriod  = FindBelief(ctx.CurrentBeliefs, "notice_period")?.Value;
        var autoRenewal   = FindBelief(ctx.CurrentBeliefs, "auto_renewal")?.Value;

        DateTimeOffset? renewalDate = renewalDateRaw.HasValue
            ? DateTimeOffset.FromUnixTimeSeconds((long)renewalDateRaw.Value)
            : null;

        var sb = new StringBuilder();
        sb.Append($"Active contract: {Path.GetFileName(current.Ref)}");

        if (annualValue.HasValue)
            sb.Append($". Annual value: £{annualValue.Value.ToString("N0", CultureInfo.InvariantCulture)}");

        if (renewalDate.HasValue)
        {
            var daysUntil = (int)(renewalDate.Value - ctx.Now).TotalDays;
            sb.Append($". Renews: {renewalDate.Value:yyyy-MM-dd} ({daysUntil} days)");
        }

        if (noticePeriod.HasValue)
        {
            var noticeDays = (int)noticePeriod.Value;
            sb.Append($". Notice period: {noticeDays} days");

            if (renewalDate.HasValue)
            {
                var noticeDeadline = renewalDate.Value.AddDays(-noticeDays);
                var daysToDeadline = (int)(noticeDeadline - ctx.Now).TotalDays;
                if (daysToDeadline <= 30)
                    sb.Append($". ⚠ Notice deadline: {noticeDeadline:yyyy-MM-dd} ({daysToDeadline} days)");
            }
        }

        if (autoRenewal.HasValue)
            sb.Append(autoRenewal.Value >= 1.0 ? ". Auto-renewal: active" : ". Auto-renewal: off");

        sb.Append('.');

        return new BriefingSection(
            Heading:          "CONTRACT POSITION",
            Content:          sb.ToString(),
            SourceReferences: [citeId]);
    }

    private static BriefingSection ComposeRecentDevelopments(
        VendorCallBriefingContext ctx, CitationBuilder citations)
    {
        if (ctx.RecentEmails.Count == 0)
        {
            var fallbackId = citations.Register(
                ctx.Meeting.ExternalId,
                $"Calendar event: {ctx.Meeting.Subject}",
                ctx.Meeting.StartUtc);
            return new BriefingSection(
                Heading:          "RECENT DEVELOPMENTS",
                Content:          "No recent commercial emails found.",
                SourceReferences: [fallbackId]);
        }

        // Group by conversation thread, sorted by most-recent email descending
        var threads = ctx.RecentEmails
            .GroupBy(e => e.ConversationId)
            .Select(g =>
            {
                var sorted  = g.OrderByDescending(e => e.SentAtUtc).ToList();
                var latest  = sorted[0];
                var earliest = sorted[^1];
                return (latest, earliest, sorted);
            })
            .OrderByDescending(t => t.latest.SentAtUtc)
            .Take(6)   // cap threads shown
            .ToList();

        var sb      = new StringBuilder();
        var citeIds = new List<string>();

        foreach (var (latest, earliest, sorted) in threads)
        {
            var citeId = citations.Register(
                sourceId:    latest.ExternalId,
                description: $"Email thread: {latest.Subject} ({latest.SentAtUtc:yyyy-MM-dd})",
                date:        latest.SentAtUtc);
            citeIds.Add(citeId);

            var dateRange = earliest.SentAtUtc.Date == latest.SentAtUtc.Date
                ? latest.SentAtUtc.ToString("MMM d")
                : $"{earliest.SentAtUtc:MMM d}–{latest.SentAtUtc:MMM d}";

            var preview = latest.BodyPreview.Length > 100
                ? latest.BodyPreview[..100] + "..."
                : latest.BodyPreview;

            sb.AppendLine($"• [{dateRange}] {latest.Subject} ({sorted.Count} message(s)).");
            sb.AppendLine($"  Latest: \"{preview}\"");
        }

        return new BriefingSection(
            Heading:          "RECENT DEVELOPMENTS",
            Content:          sb.ToString().TrimEnd(),
            SourceReferences: citeIds);
    }

    private static BriefingSection ComposeOpenCommitments(
        VendorCallBriefingContext ctx, CitationBuilder citations)
    {
        if (ctx.OpenCommitments.Count == 0)
        {
            var fallbackId = citations.Register(
                ctx.Meeting.ExternalId,
                $"Calendar event: {ctx.Meeting.Subject}",
                ctx.Meeting.StartUtc);
            return new BriefingSection(
                Heading:          "OPEN COMMITMENTS",
                Content:          "No open commitments recorded.",
                SourceReferences: [fallbackId]);
        }

        var sb      = new StringBuilder();
        var citeIds = new List<string>();

        foreach (var commitment in ctx.OpenCommitments.OrderBy(c => c.IngestedAt))
        {
            var citeId = citations.Register(
                sourceId:    commitment.EvidenceId.ToString(),
                description: $"Commitment: {CommitmentLabel(commitment.Ref)}",
                date:        commitment.IngestedAt);
            citeIds.Add(citeId);

            var ageDays   = (int)(ctx.Now - commitment.IngestedAt).TotalDays;
            var overdue   = ageDays > 4;
            var label     = CommitmentLabel(commitment.Ref);
            var ageStr    = ageDays == 1 ? "1 day" : $"{ageDays} days";
            var overdueMarker = overdue ? "⚠ " : "";
            sb.AppendLine($"• {overdueMarker}{label} — recorded {ageStr} ago{(overdue ? " (OVERDUE)" : "")}.");
        }

        return new BriefingSection(
            Heading:          "OPEN COMMITMENTS",
            Content:          sb.ToString().TrimEnd(),
            SourceReferences: citeIds);
    }

    private static BriefingSection ComposeRisksAndOpportunities(
        VendorCallBriefingContext ctx, CitationBuilder citations)
    {
        var sb      = new StringBuilder();
        var citeIds = new List<string>();

        // ── Pricing signal ────────────────────────────────────────────────────
        foreach (var signal in ctx.CommercialSignals)
        {
            var citeId = citations.Register(
                sourceId:    signal.EvidenceId.ToString(),
                description: $"Commercial signal: {Path.GetFileNameWithoutExtension(signal.Ref)}",
                date:        signal.IngestedAt);
            citeIds.Add(citeId);
            sb.AppendLine($"• Risk: pricing or commercial signal — {CommitmentLabel(signal.Ref)}.");
        }

        // ── Renewal window ────────────────────────────────────────────────────
        var renewalDateRaw = FindBelief(ctx.CurrentBeliefs, "renewal_date")?.Value;
        var noticePeriod   = FindBelief(ctx.CurrentBeliefs, "notice_period")?.Value;

        if (renewalDateRaw.HasValue && noticePeriod.HasValue)
        {
            var renewalDate    = DateTimeOffset.FromUnixTimeSeconds((long)renewalDateRaw.Value);
            var noticeDays     = (int)noticePeriod.Value;
            var noticeDeadline = renewalDate.AddDays(-noticeDays);
            var daysToDeadline = (int)(noticeDeadline - ctx.Now).TotalDays;

            if (daysToDeadline <= 30)
            {
                var contractSource = ctx.Contracts.Count > 0
                    ? ctx.Contracts.OrderByDescending(c => c.IngestedAt).First()
                    : null;

                if (contractSource is not null)
                {
                    var citeId = citations.Register(
                        sourceId:    contractSource.EvidenceId.ToString(),
                        description: $"Signed contract: {Path.GetFileName(contractSource.Ref)}",
                        date:        contractSource.IngestedAt);
                    if (!citeIds.Contains(citeId)) citeIds.Add(citeId);
                }

                sb.AppendLine(
                    $"• Risk: renewal window narrowing — " +
                    $"{daysToDeadline} days to notice deadline ({noticeDeadline:yyyy-MM-dd}).");
            }
        }

        // ── Overdue commitments ───────────────────────────────────────────────
        foreach (var commitment in ctx.OpenCommitments)
        {
            var ageDays = (int)(ctx.Now - commitment.IngestedAt).TotalDays;
            if (ageDays > 4)
            {
                var citeId = citations.Register(
                    sourceId:    commitment.EvidenceId.ToString(),
                    description: $"Commitment: {CommitmentLabel(commitment.Ref)}",
                    date:        commitment.IngestedAt);
                if (!citeIds.Contains(citeId)) citeIds.Add(citeId);
                sb.AppendLine($"• Risk: overdue commitment — {CommitmentLabel(commitment.Ref)} ({ageDays} days).");
            }
        }

        // ── Renewal intent ────────────────────────────────────────────────────
        var renewalIntent = FindBelief(ctx.CurrentBeliefs, "renewal_intent")?.Value;
        if (renewalIntent.HasValue && renewalIntent.Value < 0.70)
        {
            sb.AppendLine(
                $"• Concern: renewal intent signal below threshold " +
                $"(score {renewalIntent.Value:F2}) — vendor relationship at risk.");
        }

        if (sb.Length == 0)
        {
            sb.Append("No specific risks or opportunities identified from current evidence.");
            var fallbackId = citations.Register(
                ctx.Meeting.ExternalId,
                $"Calendar event: {ctx.Meeting.Subject}",
                ctx.Meeting.StartUtc);
            citeIds.Add(fallbackId);
        }

        // Must have at least one source reference
        if (citeIds.Count == 0)
        {
            var fallbackId = citations.Register(
                ctx.Meeting.ExternalId,
                $"Calendar event: {ctx.Meeting.Subject}",
                ctx.Meeting.StartUtc);
            citeIds.Add(fallbackId);
        }

        return new BriefingSection(
            Heading:          "RISKS & OPPORTUNITIES",
            Content:          sb.ToString().TrimEnd(),
            SourceReferences: citeIds);
    }

    private static BriefingSection ComposeEvidenceGaps(
        VendorCallBriefingContext ctx, CitationBuilder citations)
    {
        var meetingCiteId = citations.Register(
            ctx.Meeting.ExternalId,
            $"Calendar event: {ctx.Meeting.Subject}",
            ctx.Meeting.StartUtc);

        if (ctx.EvidenceGaps.Count == 0)
        {
            return new BriefingSection(
                Heading:          "EVIDENCE GAPS",
                Content:          "No evidence gaps identified.",
                SourceReferences: [meetingCiteId]);
        }

        var content = string.Join(Environment.NewLine,
            ctx.EvidenceGaps.Select(g => $"• {g}"));

        return new BriefingSection(
            Heading:          "EVIDENCE GAPS",
            Content:          content,
            SourceReferences: [meetingCiteId]);
    }

    private static BriefingSection ComposeRecommendedQuestions(
        VendorCallBriefingContext ctx, CitationBuilder citations)
    {
        var questions = new List<(string Text, string SourceId)>();
        var cap       = ctx.Recipe.Limits.MaximumQuestionsInBriefing;

        // ── Pricing signal question ───────────────────────────────────────────
        if (ctx.CommercialSignals.Count > 0)
        {
            var signal = ctx.CommercialSignals.OrderByDescending(s => s.IngestedAt).First();
            var citeId = citations.Register(
                signal.EvidenceId.ToString(),
                $"Commercial signal: {Path.GetFileNameWithoutExtension(signal.Ref)}",
                signal.IngestedAt);
            questions.Add(("What is the justification for the proposed pricing change?", citeId));
        }

        // ── Overdue commitment questions ──────────────────────────────────────
        foreach (var commitment in ctx.OpenCommitments.OrderBy(c => c.IngestedAt))
        {
            if (questions.Count >= cap) break;
            var ageDays = (int)(ctx.Now - commitment.IngestedAt).TotalDays;
            if (ageDays > 4)
            {
                var citeId = citations.Register(
                    commitment.EvidenceId.ToString(),
                    $"Commitment: {CommitmentLabel(commitment.Ref)}",
                    commitment.IngestedAt);
                questions.Add(($"What is the current status of: {CommitmentLabel(commitment.Ref)}?", citeId));
            }
        }

        // ── Renewal deadline question ─────────────────────────────────────────
        if (questions.Count < cap)
        {
            var renewalDateRaw = FindBelief(ctx.CurrentBeliefs, "renewal_date")?.Value;
            var noticePeriod   = FindBelief(ctx.CurrentBeliefs, "notice_period")?.Value;

            if (renewalDateRaw.HasValue && noticePeriod.HasValue)
            {
                var renewalDate    = DateTimeOffset.FromUnixTimeSeconds((long)renewalDateRaw.Value);
                var noticeDeadline = renewalDate.AddDays(-(int)noticePeriod.Value);
                var contractSource = ctx.Contracts.Count > 0
                    ? ctx.Contracts.OrderByDescending(c => c.IngestedAt).First()
                    : null;

                var citeId = contractSource is not null
                    ? citations.Register(
                        contractSource.EvidenceId.ToString(),
                        $"Signed contract: {Path.GetFileName(contractSource.Ref)}",
                        contractSource.IngestedAt)
                    : citations.Register(
                        ctx.Meeting.ExternalId,
                        $"Calendar event: {ctx.Meeting.Subject}",
                        ctx.Meeting.StartUtc);

                questions.Add((
                    $"Can you confirm the renewal terms ahead of the {noticeDeadline:yyyy-MM-dd} notice deadline?",
                    citeId));
            }
        }

        // ── Prior meeting follow-ups ──────────────────────────────────────────
        foreach (var note in ctx.PriorMeetingNotes.OrderByDescending(n => n.IngestedAt))
        {
            if (questions.Count >= cap) break;
            var citeId = citations.Register(
                note.EvidenceId.ToString(),
                $"Meeting note: {Path.GetFileNameWithoutExtension(note.Ref)}",
                note.IngestedAt);
            questions.Add(($"Can you provide an update on actions from the {note.IngestedAt:yyyy-MM-dd} meeting?", citeId));
        }

        // ── Fallback if still empty ───────────────────────────────────────────
        if (questions.Count == 0)
        {
            var fallbackId = citations.Register(
                ctx.Meeting.ExternalId,
                $"Calendar event: {ctx.Meeting.Subject}",
                ctx.Meeting.StartUtc);
            questions.Add(("What are the key outcomes you are looking to achieve from this meeting?", fallbackId));
        }

        var capped  = questions.Take(cap).ToList();
        var content = string.Join(Environment.NewLine,
            capped.Select((q, i) => $"{i + 1}. {q.Text}"));

        return new BriefingSection(
            Heading:          "RECOMMENDED QUESTIONS",
            Content:          content,
            SourceReferences: capped.Select(q => q.SourceId).Distinct().ToList());
    }

    private static BriefingSection ComposeSafestNextAction(
        VendorCallBriefingContext ctx, CitationBuilder citations)
    {
        // Priority 1: notice deadline imminent (within 30 days)
        var renewalDateRaw = FindBelief(ctx.CurrentBeliefs, "renewal_date")?.Value;
        var noticePeriod   = FindBelief(ctx.CurrentBeliefs, "notice_period")?.Value;

        if (renewalDateRaw.HasValue && noticePeriod.HasValue)
        {
            var renewalDate    = DateTimeOffset.FromUnixTimeSeconds((long)renewalDateRaw.Value);
            var noticeDeadline = renewalDate.AddDays(-(int)noticePeriod.Value);
            var daysToDeadline = (int)(noticeDeadline - ctx.Now).TotalDays;

            if (daysToDeadline <= 30)
            {
                var contractSource = ctx.Contracts.Count > 0
                    ? ctx.Contracts.OrderByDescending(c => c.IngestedAt).First()
                    : null;

                var citeId = contractSource is not null
                    ? citations.Register(
                        contractSource.EvidenceId.ToString(),
                        $"Signed contract: {Path.GetFileName(contractSource.Ref)}",
                        contractSource.IngestedAt)
                    : citations.Register(
                        ctx.Meeting.ExternalId,
                        $"Calendar event: {ctx.Meeting.Subject}",
                        ctx.Meeting.StartUtc);

                return new BriefingSection(
                    Heading:          "SAFEST NEXT ACTION",
                    Content:          $"Confirm renewal or non-renewal position before {noticeDeadline:yyyy-MM-dd}. " +
                                      $"Notice deadline is {daysToDeadline} day(s) away — " +
                                      $"auto-renewal may trigger if no action is taken.",
                    SourceReferences: [citeId]);
            }
        }

        // Priority 2: overdue commitment blocking progress
        var overdueCommitment = ctx.OpenCommitments
            .Where(c => (int)(ctx.Now - c.IngestedAt).TotalDays > 4)
            .OrderBy(c => c.IngestedAt)
            .FirstOrDefault();

        if (overdueCommitment is not null)
        {
            var citeId = citations.Register(
                overdueCommitment.EvidenceId.ToString(),
                $"Commitment: {CommitmentLabel(overdueCommitment.Ref)}",
                overdueCommitment.IngestedAt);

            return new BriefingSection(
                Heading:          "SAFEST NEXT ACTION",
                Content:          $"Request resolution of overdue commitment before proceeding: " +
                                  $"{CommitmentLabel(overdueCommitment.Ref)}.",
                SourceReferences: [citeId]);
        }

        // Priority 3: pricing signal unaddressed
        if (ctx.CommercialSignals.Count > 0)
        {
            var signal = ctx.CommercialSignals.OrderByDescending(s => s.IngestedAt).First();
            var citeId = citations.Register(
                signal.EvidenceId.ToString(),
                $"Commercial signal: {Path.GetFileNameWithoutExtension(signal.Ref)}",
                signal.IngestedAt);

            return new BriefingSection(
                Heading:          "SAFEST NEXT ACTION",
                Content:          $"Request formal written pricing proposal and benchmark " +
                                  $"against alternatives before committing to renewal terms.",
                SourceReferences: [citeId]);
        }

        // Default
        var defaultCiteId = citations.Register(
            ctx.Meeting.ExternalId,
            $"Calendar event: {ctx.Meeting.Subject}",
            ctx.Meeting.StartUtc);

        return new BriefingSection(
            Heading:          "SAFEST NEXT ACTION",
            Content:          $"Review open items with {ctx.VendorName} and confirm next steps in writing.",
            SourceReferences: [defaultCiteId]);
    }

    // ── Mode B LLM rephrase (degrades to Mode A on any failure) ──────────────

    private static async Task<BriefingSection> TryRephrase(
        BriefingSection section,
        IKozmoLlm       llm,
        CancellationToken ct)
    {
        try
        {
            const string system =
                "You are a procurement briefing assistant. " +
                "Rephrase the following briefing section for clarity and readability. " +
                "Do not add any information not in the input. " +
                "Do not remove any information. " +
                "Keep it concise and professional. " +
                "Respond with JSON: {\"text\": \"<rephrased text>\"}";

            var result = await llm.CompleteJsonAsync(system, section.Content, maxTokens: 400, ct: ct);

            if (result.Answer is JsonElement el &&
                el.TryGetProperty("text", out var textEl))
            {
                var rephrased = textEl.GetString();
                if (!string.IsNullOrWhiteSpace(rephrased))
                    return section with { Content = rephrased };
            }
        }
        catch
        {
            // Any failure (LlmCacheMissException, timeout, etc.) → Mode A fallback
        }

        return section;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static Belief? FindBelief(IReadOnlyList<Belief> beliefs, string claimKey)
        => beliefs.FirstOrDefault(b =>
               string.Equals(b.ClaimKey, claimKey, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Derives a human-readable label from an evidence Ref path.
    /// E.g. "notes/northstar-commitment-sla-report-request-2026-07-10.txt"
    ///   → "sla report request"
    /// </summary>
    private static string CommitmentLabel(string refPath)
    {
        var name = Path.GetFileNameWithoutExtension(refPath);

        // Split on hyphens, skip vendor prefix tokens and date tokens
        var parts    = name.Split('-');
        var labelParts = new List<string>();
        var skipNext = false;

        for (var i = 0; i < parts.Length; i++)
        {
            var p = parts[i];
            // Skip 4-digit years and 2-digit month/day sequences (date tokens)
            if (p.Length == 4 && int.TryParse(p, out var year) && year > 2000) break;
            if (p.Length == 2 && int.TryParse(p, out _) && i > 3) { skipNext = true; continue; }
            if (skipNext) { skipNext = false; continue; }

            // Skip known vendor prefix ("northstar") and common classifiers
            if (i == 0 && p.Equals("northstar", StringComparison.OrdinalIgnoreCase)) continue;
            if (p.Equals("commitment", StringComparison.OrdinalIgnoreCase)) continue;
            if (p.Equals("msa", StringComparison.OrdinalIgnoreCase)) continue;
            if (p.Equals("meeting", StringComparison.OrdinalIgnoreCase)) continue;
            if (p.Equals("notes", StringComparison.OrdinalIgnoreCase)) continue;

            labelParts.Add(p);
        }

        var label = string.Join(" ", labelParts);
        return string.IsNullOrWhiteSpace(label) ? Path.GetFileName(refPath) : label;
    }
}

// ── Citation builder ──────────────────────────────────────────────────────────

/// <summary>
/// Collects unique sources across all sections and assigns sequential citation indices.
/// </summary>
internal sealed class CitationBuilder
{
    private readonly List<(string SourceId, string Description, DateTimeOffset Date)> _items = [];
    private readonly HashSet<string> _seen = [];

    /// <summary>
    /// Registers a source and returns its SourceId (for use in SourceReferences lists).
    /// Idempotent: registering the same SourceId twice keeps the first entry.
    /// </summary>
    public string Register(string sourceId, string description, DateTimeOffset date)
    {
        if (_seen.Add(sourceId))
            _items.Add((sourceId, description, date));
        return sourceId;
    }

    public IReadOnlyList<BriefingCitation> Build()
        => _items.Select((item, i) => new BriefingCitation(
               Index:             i + 1,
               SourceDescription: item.Description,
               SourceId:          item.SourceId,
               SourceDate:        item.Date))
           .ToList();

    /// <summary>Returns the 1-based citation index for a SourceId, or -1 if not registered.</summary>
    public int IndexOf(string sourceId)
    {
        var idx = _items.FindIndex(x => x.SourceId == sourceId);
        return idx >= 0 ? idx + 1 : -1;
    }
}
