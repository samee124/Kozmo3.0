using System.Text.Json;
using Kozmo.Llm;

namespace Ii.CandidateExtraction;

/// <summary>
/// Vision-based OCR fallback. Calls <see cref="IKozmoLlm.CompleteVisionAsync"/> for each
/// page image and concatenates the results into a single text string.
/// <para>
/// Returns <see langword="null"/> when:
/// <list type="bullet">
///   <item>The underlying <see cref="IKozmoLlm"/> does not support vision
///     (<see cref="NotSupportedException"/> is swallowed — caller treats doc as unreadable).</item>
///   <item><paramref name="pageImages"/> is empty — nothing to process.</item>
/// </list>
/// <see cref="LlmCacheMissException"/> is <b>not</b> caught here; the caller (runner) catches
/// it to implement the soft-edge fall-through to the unreadable list.
/// </para>
/// </summary>
public sealed class OcrExtractor
{
    private readonly IKozmoLlm _llm;

    public OcrExtractor(IKozmoLlm llm) => _llm = llm;

    public async Task<string?> ExtractTextAsync(
        IReadOnlyList<byte[]> pageImages,
        CancellationToken     ct = default)
    {
        if (pageImages.Count == 0) return null;

        // NotSupportedException and LlmCacheMissException are NOT caught here;
        // both propagate to the caller (runner) so it can record a specific failure reason.
        var pages = new List<string>(pageImages.Count);
        foreach (var img in pageImages)
        {
            var result   = await _llm.CompleteVisionAsync(OcrPrompt.System, img, OcrPrompt.MaxTokens, ct);
            var pageText = result.Answer is JsonElement el ? (el.GetString() ?? "") : "";
            if (!string.IsNullOrWhiteSpace(pageText))
                pages.Add(pageText);
        }

        return pages.Count > 0 ? string.Join("\n", pages) : null;
    }
}
