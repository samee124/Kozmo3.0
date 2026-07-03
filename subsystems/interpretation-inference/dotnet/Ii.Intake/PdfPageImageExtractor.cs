using System.Reflection;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace Ii.Intake;

/// <summary>
/// Extracts raster images from image-only PDFs (scanned documents).
/// Handles both PNG-exportable images (via PdfPig's TryGetPng) and the more common
/// DCT/JPEG-compressed scans where TryGetPng returns false — raw JPEG bytes are read
/// from XObjectImage.RawMemory (confirmed FF D8 SOI marker before use).
/// </summary>
public sealed class PdfPageImageExtractor
{
    /// <summary>Maximum pages extracted by default. Party names appear in the first 1–2 pages
    /// of any contract or DPA; 3 is sufficient and caps vision API calls at a predictable cost.</summary>
    public const int DefaultMaxPages = 3;

    // PdfPig does not expose RawMemory on IPdfImage; read it from the concrete XObjectImage.
    private static readonly PropertyInfo? RawMemoryProp =
        typeof(IPdfImage).Assembly
            .GetType("UglyToad.PdfPig.XObjects.XObjectImage")
            ?.GetProperty("RawMemory", BindingFlags.Public | BindingFlags.Instance);

    /// <summary>
    /// Returns up to <paramref name="maxPages"/> image byte arrays (PNG or JPEG) extracted
    /// from the PDF. Returns an empty list for text-layer documents (no embedded rasters).
    /// Each element is either a PNG (89 50 4E 47 header) or a JPEG (FF D8 header); callers
    /// should detect the format from the first two bytes before choosing a MIME type.
    /// </summary>
    public IReadOnlyList<byte[]> ExtractPageImages(byte[] pdfBytes, int maxPages = DefaultMaxPages)
    {
        ArgumentNullException.ThrowIfNull(pdfBytes);
        if (maxPages <= 0) throw new ArgumentOutOfRangeException(nameof(maxPages));

        var result = new List<byte[]>();
        using var doc = PdfDocument.Open(pdfBytes);
        foreach (var page in doc.GetPages())
        {
            if (result.Count >= maxPages) break;

            foreach (var image in page.GetImages())
            {
                // Primary path: images PdfPig can natively export as PNG.
                if (image.TryGetPng(out var png) && png.Length > 0)
                {
                    result.Add(png);
                    break;
                }

                // Fallback: DCT/JPEG-compressed scans — TryGetPng returns false.
                // XObjectImage.RawMemory holds the raw compressed bytes; detect JPEG via FF D8.
                var raw = TryGetRawJpeg(image);
                if (raw != null)
                {
                    result.Add(raw);
                    break;
                }
            }
        }

        return result;
    }

    private static byte[]? TryGetRawJpeg(IPdfImage image)
    {
        if (RawMemoryProp == null) return null;
        try
        {
            if (RawMemoryProp.GetValue(image) is not ReadOnlyMemory<byte> mem || mem.Length < 2)
                return null;

            var raw = mem.ToArray();
            return raw[0] == 0xFF && raw[1] == 0xD8 ? raw : null; // FF D8 = JPEG SOI marker
        }
        catch
        {
            return null;
        }
    }
}
