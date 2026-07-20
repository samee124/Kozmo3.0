using System.Text;

namespace Po.VendorCall;

/// <summary>
/// Renders a VendorCallBriefing as a readable plain-text string suitable for
/// console output or email body.
/// </summary>
public static class BriefingTextRenderer
{
    private const string Heavy = "══════════════════════════════════════════════════";
    private const string Light = "──────────────────────────────────────────────────";

    public static string Render(VendorCallBriefing briefing)
    {
        // Build a lookup from SourceId → citation index for inline reference resolution
        var indexLookup = briefing.Citations.ToDictionary(c => c.SourceId, c => c.Index);

        var sb = new StringBuilder();

        // ── Header ────────────────────────────────────────────────────────────
        sb.AppendLine(Heavy);
        sb.AppendLine($"PRE-MEETING BRIEF: {briefing.VendorName}");
        sb.AppendLine($"{briefing.MeetingSubject} — {briefing.MeetingTime:yyyy-MM-dd HH:mm} UTC");

        if (briefing.Attendees.Count > 0)
        {
            var attendeeList = string.Join(", ", briefing.Attendees.Take(4));
            if (briefing.Attendees.Count > 4)
                attendeeList += $" (+{briefing.Attendees.Count - 4} more)";
            sb.AppendLine($"Attendees: {attendeeList}");
        }

        sb.AppendLine(Heavy);

        // ── Sections ──────────────────────────────────────────────────────────
        var sections = new[]
        {
            briefing.MeetingObjective,
            briefing.ContractPosition,
            briefing.RecentDevelopments,
            briefing.OpenCommitments,
            briefing.RisksAndOpportunities,
            briefing.EvidenceGaps,
            briefing.RecommendedQuestions,
            briefing.SafestNextAction,
        };

        foreach (var section in sections)
            AppendSection(sb, section, indexLookup);

        // ── Sources ───────────────────────────────────────────────────────────
        sb.AppendLine(Light);
        sb.AppendLine("SOURCES");
        foreach (var citation in briefing.Citations)
            sb.AppendLine($"[{citation.Index}] {citation.SourceDescription} ({citation.SourceDate:yyyy-MM-dd})");
        sb.Append(Light);

        return sb.ToString();
    }

    private static void AppendSection(
        StringBuilder                 sb,
        BriefingSection               section,
        Dictionary<string, int>       indexLookup)
    {
        sb.AppendLine();
        sb.AppendLine(section.Heading);

        // Word-wrap content at ~80 chars
        foreach (var line in section.Content.Split('\n'))
        {
            var trimmed = line.TrimEnd();
            if (trimmed.Length > 0)
                sb.AppendLine(trimmed);
        }

        // Append source citations
        var indices = section.SourceReferences
            .Select(id => indexLookup.TryGetValue(id, out var idx) ? idx : -1)
            .Where(i => i > 0)
            .Distinct()
            .OrderBy(i => i)
            .ToList();

        if (indices.Count > 0)
        {
            var refs = string.Join(", ", indices);
            sb.AppendLine($"[Sources: {refs}]");
        }
    }
}
