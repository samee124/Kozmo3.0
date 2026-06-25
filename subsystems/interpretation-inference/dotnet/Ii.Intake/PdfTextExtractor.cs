using UglyToad.PdfPig;

namespace Ii.Intake;

/// <summary>
/// Converts PDF bytes into a 1-based page-number → extracted-text map.
///
/// Called by seed-prep tooling only; never invoked in the demo-runtime pipeline.
/// The resulting dictionary is passed to VendorFilePdfLane.ExtractAndWriteAsync
/// whose LLM call then classifies the page texts into structured claims.
/// </summary>
public sealed class PdfTextExtractor
{
    /// <summary>
    /// Opens <paramref name="pdfBytes"/> and returns page-number → word-joined text.
    /// Page numbers are 1-based and match PDF logical page order.
    /// Pages that contain no extractable text are included with an empty string.
    /// </summary>
    public IReadOnlyDictionary<int, string> ExtractPageTexts(byte[] pdfBytes)
    {
        ArgumentNullException.ThrowIfNull(pdfBytes);

        var result = new Dictionary<int, string>();

        using var doc = PdfDocument.Open(pdfBytes);
        foreach (var page in doc.GetPages())
        {
            var text = string.Join(" ", page.GetWords().Select(w => w.Text));
            result[page.Number] = text;
        }

        return result;
    }
}
