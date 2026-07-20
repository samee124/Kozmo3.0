using System.Globalization;
using System.Text.RegularExpressions;

namespace Po.VendorCall;

public interface ITranscriptParser
{
    TranscriptParseResult Parse(string vttContent);
}

public sealed record TranscriptParseResult(
    IReadOnlyList<TranscriptSegment> Segments,
    IReadOnlyList<string>            Speakers,
    TimeSpan                         TotalDuration,
    int                              TotalWordCount);

public sealed record TranscriptSegment(
    string   Speaker,
    TimeSpan StartTime,
    TimeSpan EndTime,
    string   Text);

/// <summary>
/// Deterministic VTT transcript parser. No LLM, no external calls.
///
/// Handles:
///   - WEBVTT header and blank-line-separated cue blocks
///   - Optional cue identifier lines before the timestamp line
///   - Timestamp format: HH:MM:SS.mmm --> HH:MM:SS.mmm (dot or comma decimal separator)
///   - Speaker tags: &lt;v SpeakerName&gt;text&lt;/v&gt;
///   - Multi-line cue content
///   - Cues with no speaker tag (speaker = "Unknown")
///   - Merges consecutive cues by the same speaker into one segment
/// </summary>
public sealed class TranscriptParser : ITranscriptParser
{
    private static readonly Regex TimestampLine = new(
        @"^(\d{2}:\d{2}:\d{2}[.,]\d{3})\s+-->\s+(\d{2}:\d{2}:\d{2}[.,]\d{3})",
        RegexOptions.Compiled);

    private static readonly Regex SpeakerTag = new(
        @"<v\s+([^>]+)>",
        RegexOptions.Compiled);

    private static readonly Regex HtmlTag = new(
        @"<[^>]+>",
        RegexOptions.Compiled);

    public TranscriptParseResult Parse(string vttContent)
    {
        if (string.IsNullOrWhiteSpace(vttContent))
            return new TranscriptParseResult([], [], TimeSpan.Zero, 0);

        var raw      = ParseCues(vttContent);
        var merged   = MergeConsecutiveSpeaker(raw);
        var speakers = merged.Select(s => s.Speaker).Distinct().OrderBy(s => s).ToList();

        var totalDuration = merged.Count > 0
            ? merged[^1].EndTime - merged[0].StartTime
            : TimeSpan.Zero;

        var wordCount = merged.Sum(s => CountWords(s.Text));

        return new TranscriptParseResult(merged, speakers, totalDuration, wordCount);
    }

    // ── Cue parsing ───────────────────────────────────────────────────────────

    private static List<TranscriptSegment> ParseCues(string vttContent)
    {
        // Normalise line endings then split into blocks on double-blank-line
        var normalised = vttContent.Replace("\r\n", "\n").Replace("\r", "\n");
        var blocks     = normalised.Split("\n\n", StringSplitOptions.RemoveEmptyEntries);
        var segments   = new List<TranscriptSegment>();

        foreach (var block in blocks)
        {
            var lines = block.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length == 0) continue;

            // Skip the WEBVTT header block
            if (lines[0].TrimStart().StartsWith("WEBVTT", StringComparison.OrdinalIgnoreCase))
                continue;

            // Find the timestamp line — may be preceded by a cue identifier
            var tsIdx = -1;
            for (var i = 0; i < lines.Length; i++)
            {
                if (TimestampLine.IsMatch(lines[i]))
                {
                    tsIdx = i;
                    break;
                }
            }

            if (tsIdx < 0) continue; // no timestamp found — malformed block

            var tsMatch = TimestampLine.Match(lines[tsIdx]);
            if (!TryParseVttTime(tsMatch.Groups[1].Value, out var start)) continue;
            if (!TryParseVttTime(tsMatch.Groups[2].Value, out var end))   continue;

            // All lines after the timestamp are content
            var contentLines = lines.Skip(tsIdx + 1).ToArray();
            if (contentLines.Length == 0) continue;

            var rawContent = string.Join(" ", contentLines).Trim();
            if (string.IsNullOrWhiteSpace(rawContent)) continue;

            var speaker = ExtractSpeaker(rawContent);
            var text    = StripTags(rawContent).Trim();

            if (string.IsNullOrWhiteSpace(text)) continue;

            segments.Add(new TranscriptSegment(speaker, start, end, text));
        }

        return segments;
    }

    // ── Merging ───────────────────────────────────────────────────────────────

    private static List<TranscriptSegment> MergeConsecutiveSpeaker(List<TranscriptSegment> segments)
    {
        if (segments.Count == 0) return [];

        var merged  = new List<TranscriptSegment>();
        var current = segments[0];

        for (var i = 1; i < segments.Count; i++)
        {
            var next = segments[i];
            if (next.Speaker == current.Speaker)
            {
                current = current with
                {
                    EndTime = next.EndTime,
                    Text    = current.Text + " " + next.Text
                };
            }
            else
            {
                merged.Add(current);
                current = next;
            }
        }

        merged.Add(current);
        return merged;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string ExtractSpeaker(string content)
    {
        var m = SpeakerTag.Match(content);
        return m.Success ? m.Groups[1].Value.Trim() : "Unknown";
    }

    private static string StripTags(string content) => HtmlTag.Replace(content, "");

    private static bool TryParseVttTime(string s, out TimeSpan result)
    {
        s = s.Replace(',', '.'); // normalise comma decimal separator (some VTT generators use ,)
        return TimeSpan.TryParseExact(s, @"hh\:mm\:ss\.fff",
            CultureInfo.InvariantCulture, out result);
    }

    private static int CountWords(string text)
        => string.IsNullOrWhiteSpace(text)
            ? 0
            : text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
}
