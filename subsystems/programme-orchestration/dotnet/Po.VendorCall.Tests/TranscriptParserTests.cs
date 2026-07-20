using Po.VendorCall;
using Xunit;

namespace Po.VendorCall.Tests;

public sealed class TranscriptParserTests
{
    private static readonly ITranscriptParser Parser = new TranscriptParser();

    // ── Empty / whitespace input ──────────────────────────────────────────────

    [Fact]
    public void Parse_EmptyString_ReturnsEmptyResult()
    {
        var result = Parser.Parse("");
        Assert.Empty(result.Segments);
        Assert.Empty(result.Speakers);
        Assert.Equal(TimeSpan.Zero, result.TotalDuration);
        Assert.Equal(0, result.TotalWordCount);
    }

    [Fact]
    public void Parse_WhitespaceOnly_ReturnsEmptyResult()
    {
        var result = Parser.Parse("   \n\n  ");
        Assert.Empty(result.Segments);
        Assert.Equal(TimeSpan.Zero, result.TotalDuration);
    }

    // ── Single cue ────────────────────────────────────────────────────────────

    [Fact]
    public void Parse_SingleCueWithSpeaker_ReturnsSingleSegment()
    {
        const string vtt = """
            WEBVTT

            00:00:01.000 --> 00:00:03.500
            <v Alice>Hello, how are you?</v>
            """;

        var result = Parser.Parse(vtt);

        var seg = Assert.Single(result.Segments);
        Assert.Equal("Alice", seg.Speaker);
        Assert.Equal(TimeSpan.FromMilliseconds(1000), seg.StartTime);
        Assert.Equal(TimeSpan.FromMilliseconds(3500), seg.EndTime);
        Assert.Equal("Hello, how are you?", seg.Text);
    }

    [Fact]
    public void Parse_CueWithNospeakerTag_UsesUnknown()
    {
        const string vtt = """
            WEBVTT

            00:00:01.000 --> 00:00:02.000
            Some text without speaker tag
            """;

        var result = Parser.Parse(vtt);

        var seg = Assert.Single(result.Segments);
        Assert.Equal("Unknown", seg.Speaker);
        Assert.Equal("Some text without speaker tag", seg.Text);
    }

    // ── Speaker merging ───────────────────────────────────────────────────────

    [Fact]
    public void Parse_ConsecutiveSameSpeaker_MergesIntoOneSegment()
    {
        const string vtt = """
            WEBVTT

            00:00:01.000 --> 00:00:02.000
            <v Alice>First part.</v>

            00:00:02.000 --> 00:00:03.000
            <v Alice>Second part.</v>
            """;

        var result = Parser.Parse(vtt);

        var seg = Assert.Single(result.Segments);
        Assert.Equal("Alice", seg.Speaker);
        Assert.Equal(TimeSpan.FromMilliseconds(1000), seg.StartTime);
        Assert.Equal(TimeSpan.FromMilliseconds(3000), seg.EndTime);
        Assert.Contains("First part.", seg.Text);
        Assert.Contains("Second part.", seg.Text);
    }

    [Fact]
    public void Parse_AlternatingSpeakers_DoesNotMerge()
    {
        const string vtt = """
            WEBVTT

            00:00:01.000 --> 00:00:02.000
            <v Alice>Hello.</v>

            00:00:02.000 --> 00:00:03.000
            <v Bob>Hi there.</v>

            00:00:03.000 --> 00:00:04.000
            <v Alice>How are you?</v>
            """;

        var result = Parser.Parse(vtt);

        Assert.Equal(3, result.Segments.Count);
        Assert.Equal("Alice", result.Segments[0].Speaker);
        Assert.Equal("Bob",   result.Segments[1].Speaker);
        Assert.Equal("Alice", result.Segments[2].Speaker);
    }

    // ── Speakers list ─────────────────────────────────────────────────────────

    [Fact]
    public void Parse_MultipleSpeakers_SpeakersListIsDistinctAndSorted()
    {
        const string vtt = """
            WEBVTT

            00:00:01.000 --> 00:00:02.000
            <v Charlie>Hey.</v>

            00:00:02.000 --> 00:00:03.000
            <v Alice>Hello.</v>

            00:00:03.000 --> 00:00:04.000
            <v Bob>Hi.</v>
            """;

        var result = Parser.Parse(vtt);

        Assert.Equal(["Alice", "Bob", "Charlie"], result.Speakers);
    }

    // ── Duration + word count ─────────────────────────────────────────────────

    [Fact]
    public void Parse_Duration_IsEndMinusStart_OfMergedSegments()
    {
        const string vtt = """
            WEBVTT

            00:00:10.000 --> 00:00:20.000
            <v Alice>First.</v>

            00:00:20.000 --> 00:00:30.000
            <v Bob>Second.</v>
            """;

        var result = Parser.Parse(vtt);

        Assert.Equal(TimeSpan.FromSeconds(20), result.TotalDuration);
    }

    [Fact]
    public void Parse_WordCount_CountsSpaceSeparatedTokens()
    {
        const string vtt = """
            WEBVTT

            00:00:01.000 --> 00:00:05.000
            <v Alice>one two three four five</v>
            """;

        var result = Parser.Parse(vtt);
        Assert.Equal(5, result.TotalWordCount);
    }

    // ── Comma decimal separator ───────────────────────────────────────────────

    [Fact]
    public void Parse_CommaDecimalSeparator_IsHandledCorrectly()
    {
        const string vtt = """
            WEBVTT

            00:00:01,500 --> 00:00:03,000
            <v Alice>Comma decimal test.</v>
            """;

        var result = Parser.Parse(vtt);

        var seg = Assert.Single(result.Segments);
        Assert.Equal(TimeSpan.FromMilliseconds(1500), seg.StartTime);
        Assert.Equal(TimeSpan.FromMilliseconds(3000), seg.EndTime);
    }

    // ── Cue identifier lines ──────────────────────────────────────────────────

    [Fact]
    public void Parse_CueIdentifierBeforeTimestamp_IsIgnored()
    {
        const string vtt = """
            WEBVTT

            cue-001
            00:00:01.000 --> 00:00:02.000
            <v Alice>Text with cue id.</v>
            """;

        var result = Parser.Parse(vtt);

        var seg = Assert.Single(result.Segments);
        Assert.Equal("Alice", seg.Speaker);
        Assert.Equal("Text with cue id.", seg.Text);
    }

    // ── HTML tag stripping ────────────────────────────────────────────────────

    [Fact]
    public void Parse_HtmlTagsStripped_TextIsClean()
    {
        const string vtt = """
            WEBVTT

            00:00:01.000 --> 00:00:02.000
            <v Alice><b>Bold</b> and <i>italic</i> text.</v>
            """;

        var result = Parser.Parse(vtt);

        var seg = Assert.Single(result.Segments);
        Assert.Equal("Bold and italic text.", seg.Text);
    }

    // ── Multi-line cue content ────────────────────────────────────────────────

    [Fact]
    public void Parse_MultiLineCueContent_JoinsWithSpace()
    {
        const string vtt = """
            WEBVTT

            00:00:01.000 --> 00:00:04.000
            <v Alice>First line
            second line</v>
            """;

        var result = Parser.Parse(vtt);

        var seg = Assert.Single(result.Segments);
        Assert.Contains("First line", seg.Text);
        Assert.Contains("second line", seg.Text);
    }

    // ── Malformed cues ────────────────────────────────────────────────────────

    [Fact]
    public void Parse_MalformedTimestamp_CueIsSkipped()
    {
        const string vtt = """
            WEBVTT

            00:00:01.000 --> INVALID
            <v Alice>Should be skipped.</v>

            00:00:02.000 --> 00:00:03.000
            <v Bob>Valid cue.</v>
            """;

        var result = Parser.Parse(vtt);

        var seg = Assert.Single(result.Segments);
        Assert.Equal("Bob", seg.Speaker);
    }

    // ── CRLF line endings ─────────────────────────────────────────────────────

    [Fact]
    public void Parse_CrlfLineEndings_ParsedCorrectly()
    {
        var vtt = "WEBVTT\r\n\r\n00:00:01.000 --> 00:00:02.000\r\n<v Alice>CRLF test.</v>\r\n";

        var result = Parser.Parse(vtt);

        var seg = Assert.Single(result.Segments);
        Assert.Equal("Alice", seg.Speaker);
        Assert.Equal("CRLF test.", seg.Text);
    }
}
