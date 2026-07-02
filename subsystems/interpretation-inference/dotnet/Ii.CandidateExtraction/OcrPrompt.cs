namespace Ii.CandidateExtraction;

/// <summary>
/// System prompt and token budget for the vision-based OCR call.
/// Changing System invalidates all vision cassette entries (key includes the system prompt).
/// </summary>
public static class OcrPrompt
{
    /// <summary>Token budget — generous enough for dense multi-column pages.</summary>
    public const int MaxTokens = 2000;

    public const string System =
        "You are a document text extraction system. " +
        "Extract all readable text from this document page image exactly as it appears. " +
        "Preserve line breaks, headings, table structure, and paragraph separation. " +
        "Do not summarise, interpret, reformat, or add commentary. " +
        "Return only the raw extracted text, nothing else.";
}
