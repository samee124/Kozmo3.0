using Ii.Intake;
using Xunit;

namespace Kozmo.VendorFile.Tests;

/// <summary>
/// Class P — PdfTextExtractor unit tests.
///
/// Exercises the PdfPig dependency against a committed minimal 2-page PDF fixture.
/// These tests run offline (no network) and do NOT touch any lane, store, or engine.
/// </summary>
[Trait("Class", "P")]
public sealed class PdfTextExtractorTests
{
    private static readonly string SamplePdfPath = FindSamplePdf();

    // ── P1: page count ────────────────────────────────────────────────────────

    [Fact]
    public void ExtractPageTexts_SamplePdf_ReturnsTwoPages()
    {
        var bytes  = File.ReadAllBytes(SamplePdfPath);
        var result = new PdfTextExtractor().ExtractPageTexts(bytes);

        Assert.Equal(2, result.Count);
        Assert.True(result.ContainsKey(1), "Result must contain 1-based page key 1");
        Assert.True(result.ContainsKey(2), "Result must contain 1-based page key 2");
    }

    // ── P2: known text on page 1 ──────────────────────────────────────────────

    [Fact]
    public void ExtractPageTexts_SamplePdf_Page1ContainsKozmo()
    {
        var bytes  = File.ReadAllBytes(SamplePdfPath);
        var result = new PdfTextExtractor().ExtractPageTexts(bytes);

        Assert.Contains("Kozmo",  result[1]);
        Assert.Contains("sample", result[1]);
        Assert.Contains("one",    result[1]);
    }

    // ── P3: known text on page 2 ──────────────────────────────────────────────

    [Fact]
    public void ExtractPageTexts_SamplePdf_Page2ContainsPageTwo()
    {
        var bytes  = File.ReadAllBytes(SamplePdfPath);
        var result = new PdfTextExtractor().ExtractPageTexts(bytes);

        Assert.Contains("Kozmo",  result[2]);
        Assert.Contains("sample", result[2]);
        Assert.Contains("two",    result[2]);
    }

    // ── P4: text is on the CORRECT page, not swapped ─────────────────────────

    [Fact]
    public void ExtractPageTexts_SamplePdf_PageTextsAreNotSwapped()
    {
        var bytes  = File.ReadAllBytes(SamplePdfPath);
        var result = new PdfTextExtractor().ExtractPageTexts(bytes);

        Assert.Contains("one", result[1]);
        Assert.DoesNotContain("two", result[1]);

        Assert.Contains("two", result[2]);
        Assert.DoesNotContain("one", result[2]);
    }

    // ── P5: null input throws ────────────────────────────────────────────────

    [Fact]
    public void ExtractPageTexts_NullBytes_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new PdfTextExtractor().ExtractPageTexts(null!));
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static string FindSamplePdf()
    {
        var dir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(dir))
        {
            var candidate = Path.Combine(dir, "fixtures", "pdf-samples", "sample.pdf");
            if (File.Exists(candidate)) return candidate;
            dir = Path.GetDirectoryName(dir);
        }
        throw new FileNotFoundException(
            "fixtures/pdf-samples/sample.pdf not found — ensure it is committed to the repo.");
    }
}
