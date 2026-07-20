using System.Text;

namespace Po.VendorCall;

/// <summary>
/// Renders a PostMeetingSummary as a readable plain-text string suitable for
/// console output or email body.
/// </summary>
public static class PostMeetingSummaryTextRenderer
{
    private const string Heavy = "══════════════════════════════════════════════════";
    private const string Light = "──────────────────────────────────────────────────";

    public static string Render(PostMeetingSummary summary)
    {
        var sb = new StringBuilder();

        // ── Header ────────────────────────────────────────────────────────────
        sb.AppendLine(Heavy);
        sb.AppendLine($"POST-MEETING SUMMARY: {summary.VendorName}");
        sb.AppendLine($"{summary.MeetingSubject} — {summary.MeetingTime:yyyy-MM-dd HH:mm} UTC");
        if (summary.Attendees.Count > 0)
        {
            var list = string.Join(", ", summary.Attendees.Take(4));
            if (summary.Attendees.Count > 4)
                list += $" (+{summary.Attendees.Count - 4} more)";
            sb.AppendLine($"Attendees: {list}");
        }
        sb.AppendLine(Heavy);

        // ── Sections ──────────────────────────────────────────────────────────
        foreach (var section in new[]
        {
            summary.MeetingOutcome,
            summary.DecisionsMade,
            summary.NewCommitments,
            summary.ResolvedFromPreBrief,
            summary.CommercialStateChange,
            summary.StillOpen,
            summary.RecommendedNextAction,
        })
        {
            AppendSection(sb, section);
        }

        // ── Sources ───────────────────────────────────────────────────────────
        sb.AppendLine();
        sb.AppendLine(Light);
        sb.AppendLine("SOURCES");
        foreach (var cite in summary.Citations)
        {
            if (cite.TranscriptTimestamp != null)
                sb.AppendLine($"[T:{cite.TranscriptTimestamp}] {cite.SourceDescription}");
            else
                sb.AppendLine($"[Pre] {cite.SourceDescription}" +
                    (cite.SourceDate.HasValue ? $" ({cite.SourceDate.Value:MMM dd})" : ""));
        }
        sb.AppendLine(Light);
        sb.AppendLine();
        sb.Append("⚠ Items marked with ⚠ require your confirmation.");

        return sb.ToString();
    }

    // ── Section rendering ─────────────────────────────────────────────────────

    private static void AppendSection(StringBuilder sb, SummarySection section)
    {
        sb.AppendLine();
        sb.AppendLine(section.Heading);

        if (!string.IsNullOrWhiteSpace(section.Content))
            sb.AppendLine(section.Content);

        foreach (var item in section.Items)
            sb.AppendLine(FormatItem(item));

        // Inline source reference line
        if (section.SourceReferences.Count > 0)
        {
            var refs = section.SourceReferences.Select(FormatSourceRef);
            sb.AppendLine($"[Sources: {string.Join(", ", refs)}]");
        }
    }

    private static string FormatItem(SummaryLineItem item)
    {
        var sb = new StringBuilder();

        // Prefix: ✓/✗/? items are rendered as-is; others get a bullet
        var text = item.Text;
        if (text.StartsWith('✓') || text.StartsWith('✗') || text.StartsWith('?'))
            sb.Append(text);
        else
        {
            sb.Append("• ");
            // Optionally prepend owner for commitment-style items
            if (item.Owner != null && !text.StartsWith('['))
                sb.Append($"{item.Owner}: ");
            sb.Append(text);
        }

        if (item.DueDate != null && !text.Contains(item.DueDate, StringComparison.OrdinalIgnoreCase))
            sb.Append($" — due: {item.DueDate}");
        if (item.TranscriptTimestamp != null)
            sb.Append($" [T:{item.TranscriptTimestamp}]");
        if (item.RequiresUserConfirmation)
            sb.Append(" ⚠");

        return sb.ToString();
    }

    private static string FormatSourceRef(string key) =>
        key == "pre-meeting-brief"  ? "[Pre]" :
        key.Contains(':')           ? $"[T:{key}]" : $"[{key}]";
}
