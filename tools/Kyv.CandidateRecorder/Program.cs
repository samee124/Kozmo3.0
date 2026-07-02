// Kyv.CandidateRecorder — record or verify candidate-extraction cassettes
//
// RECORD (requires OPENAI_API_KEY):
//   OPENAI_API_KEY=sk-... dotnet run --project tools/Kyv.CandidateRecorder -- \
//       --workspace "D:\June\Kozmo Workspace"
//
//   Enumerates ALL PDFs recursively (workspace\ScenarioNN\Category\file.pdf).
//   For each PDF: extracts text, calls OpenAI, writes cassette entry + extracted text.
//   Prints the raw model JSON before the filter, labelled by scenario + filename.
//
// VERIFY (no API key — replays from cassette):
//   dotnet run --project tools/Kyv.CandidateRecorder -- \
//       --workspace "D:\June\Kozmo Workspace" --verify
//
// Optional flags:
//   --cassette <path>   Override cassette file (default: fixtures/kyv/candidate-extraction.cassette.json)
//   --texts-dir <path>  Override extracted-texts root (default: fixtures/kyv/texts)

using Ii.CandidateExtraction;
using Ii.Intake;
using Ig.Contracts;
using Kozmo.Contracts;
using Kozmo.Llm;
using Kozmo.Llm.OpenAi;
using System.Text.Json;

var flags = ParseFlags(args);
if (flags.Workspace is null)
{
    Console.Error.WriteLine("[kyv-recorder] --workspace <path> is required");
    PrintUsage();
    return 1;
}
if (!Directory.Exists(flags.Workspace))
{
    Console.Error.WriteLine($"[kyv-recorder] workspace not found: {flags.Workspace}");
    return 1;
}

var repoRoot   = FindRepoRoot();
var cassette   = flags.Cassette  ?? Path.Combine(repoRoot, "fixtures", "kyv", "candidate-extraction.cassette.json");
var textsRoot  = flags.TextsDir  ?? Path.Combine(repoRoot, "fixtures", "kyv", "texts");

Directory.CreateDirectory(Path.GetDirectoryName(cassette)!);
Directory.CreateDirectory(textsRoot);

// ── Enumerate all PDFs recursively, grouped by scenario (top-level subfolder) ──

var allPdfs = Directory.EnumerateFiles(flags.Workspace, "*.pdf", SearchOption.AllDirectories)
    .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
    .ToList();

if (allPdfs.Count == 0)
{
    Console.Error.WriteLine($"[kyv-recorder] no PDFs found (recursive) in: {flags.Workspace}");
    return 1;
}

// Derive scenario name from the first path component below the workspace root.
string ScenarioOf(string fullPath)
{
    var rel   = Path.GetRelativePath(flags.Workspace, fullPath);
    var parts = rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    return parts.Length > 1 ? parts[0] : "(root)";
}

var byScenario = allPdfs
    .GroupBy(ScenarioOf)
    .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
    .ToList();

Console.WriteLine($"[kyv-recorder] workspace  : {flags.Workspace}");
Console.WriteLine($"[kyv-recorder] cassette   : {cassette}");
Console.WriteLine($"[kyv-recorder] texts-root : {textsRoot}");
Console.WriteLine($"[kyv-recorder] mode       : {(flags.Verify ? "verify (replay)" : "record")}");
Console.WriteLine($"[kyv-recorder] {allPdfs.Count} PDF(s) across {byScenario.Count} scenario(s)");
Console.WriteLine();

if (!flags.Verify && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENAI_API_KEY")))
{
    Console.Error.WriteLine("[kyv-recorder] OPENAI_API_KEY is not set — cannot record");
    Console.Error.WriteLine("[kyv-recorder] set the env var and re-run, or use --verify to replay");
    return 1;
}

IKozmoLlm baseLlm;
if (flags.Verify)
{
    if (!File.Exists(cassette))
    {
        Console.Error.WriteLine($"[kyv-recorder] cassette not found — run without --verify first");
        return 1;
    }
    baseLlm = new CachingLlmClient(cassette, recordMode: false);
}
else
{
    IKozmoLlm inner;
    try { inner = new OpenAiLlmClient(); }
    catch (InvalidOperationException ex)
    {
        Console.Error.WriteLine($"[kyv-recorder] OpenAI client error: {ex.Message}");
        return 1;
    }
    var cachingLlm = new CachingLlmClient(cassette, recordMode: true, inner: inner);
    baseLlm = new LoggingLlm(cachingLlm); // prints raw JSON before filter
}

var extractor       = new DocumentCandidateExtractor(baseLlm);
var pdfReader       = new PdfTextExtractor();
var imageExtractor  = new PdfPageImageExtractor();
var ocrExtractor    = new OcrExtractor(baseLlm);

// Per-scenario accumulator for the end summary.
var scenarioSummary = new Dictionary<string, ScenarioResult>();
var errorLog        = new List<string>();
int processedCount  = 0;

foreach (var group in byScenario)
{
    var scenario = group.Key;
    Console.WriteLine(new string('═', 72));
    Console.WriteLine($"  {scenario}  ({group.Count()} file(s))");
    Console.WriteLine(new string('═', 72));
    Console.WriteLine();

    var scenarioBeliefs = new List<CandidateIdentityBelief>();
    scenarioSummary[scenario] = new ScenarioResult(scenario, scenarioBeliefs);

    foreach (var pdfPath in group.OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
    {
        var fileName    = Path.GetFileName(pdfPath);
        var relPath     = Path.GetRelativePath(flags.Workspace, pdfPath);
        var tier        = DocTypeInferrer.InferTier(fileName);

        Console.WriteLine($"  ── {relPath}  [{tier}]");

        // ── Text extraction ──────────────────────────────────────────────────
        string text;
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(pdfPath);
            var pages = pdfReader.ExtractPageTexts(bytes);
            text = string.Join("\n", pages.OrderBy(kv => kv.Key).Select(kv => kv.Value));
        }
        catch (Exception ex)
        {
            var msg = $"[ERROR] PDF read failed: {ex.Message}  →  {relPath}";
            Console.Error.WriteLine($"  {msg}");
            errorLog.Add(msg);
            Console.WriteLine();
            continue;
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            // Attempt OCR on image-only PDFs to generate cassette entries for them.
            Console.WriteLine("  [no text layer — attempting OCR]");
            try
            {
                var pageImages = imageExtractor.ExtractPageImages(bytes);
                if (pageImages.Count > 0)
                {
                    var ocrResult = await ocrExtractor.ExtractTextAsync(pageImages);
                    if (!string.IsNullOrWhiteSpace(ocrResult))
                    {
                        text = ocrResult;
                        Console.WriteLine($"  [OCR succeeded: {text.Length:N0} chars from {pageImages.Count} page image(s)]");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  [OCR error: {ex.Message}]");
            }

            if (string.IsNullOrWhiteSpace(text))
            {
                var msg = $"[ERROR] empty text after extraction and OCR  →  {relPath}";
                Console.Error.WriteLine($"  {msg}");
                errorLog.Add(msg);
                Console.WriteLine();
                continue;
            }
        }

        Console.WriteLine($"  text: {text.Length:N0} chars");

        // ── Save extracted text for offline test replay ──────────────────────
        var relDir   = Path.GetDirectoryName(relPath) ?? "";
        var textDir  = Path.Combine(textsRoot, relDir);
        Directory.CreateDirectory(textDir);
        var textFile = Path.Combine(textDir, Path.ChangeExtension(fileName, ".txt"));
        File.WriteAllText(textFile, text);

        // ── LLM extraction (raw JSON printed by LoggingLlm in record mode) ──
        IReadOnlyList<CandidateIdentityBelief> beliefs;
        try
        {
            beliefs = await extractor.ExtractAsync(text, fileName, tier);
        }
        catch (LlmCacheMissException)
        {
            var msg = $"[ERROR] cassette miss (re-run in record mode)  →  {relPath}";
            Console.Error.WriteLine($"  {msg}");
            errorLog.Add(msg);
            Console.WriteLine();
            continue;
        }
        catch (Exception ex)
        {
            var msg = $"[ERROR] extraction failed: {ex.Message}  →  {relPath}";
            Console.Error.WriteLine($"  {msg}");
            errorLog.Add(msg);
            Console.WriteLine();
            continue;
        }

        // ── Print filtered candidates ────────────────────────────────────────
        if (beliefs.Count == 0)
        {
            Console.WriteLine("  (no candidates after filter)");
        }
        else
        {
            foreach (var b in beliefs)
                Console.WriteLine(
                    $"  + {b.RawName,-50}  role={b.RoleHint ?? "unknown",-10}" +
                    $"  conf={b.Confidence:F2}  tier={b.SourceTier}");
        }

        scenarioBeliefs.AddRange(beliefs);
        processedCount++;
        Console.WriteLine();
    }
}

// ── End summary ───────────────────────────────────────────────────────────────

Console.WriteLine();
Console.WriteLine(new string('═', 72));
Console.WriteLine($"  SUMMARY — {allPdfs.Count} PDF(s) across {byScenario.Count} scenario(s)");
Console.WriteLine(new string('═', 72));
Console.WriteLine();

foreach (var (scenario, result) in scenarioSummary.OrderBy(kv => kv.Key))
{
    var fileCount    = byScenario.First(g => g.Key == scenario).Count();
    var beliefCount  = result.Beliefs.Count;
    Console.WriteLine($"  {scenario}  ({fileCount} file(s), {beliefCount} candidate(s))");

    if (beliefCount > 0)
    {
        foreach (var b in result.Beliefs.OrderBy(b => b.RawName))
            Console.WriteLine(
                $"    {b.RawName,-50}  {b.RoleHint ?? "unknown",-10}  {b.SourceTier}");
    }
    else
    {
        Console.WriteLine("    (none)");
    }
    Console.WriteLine();
}

if (errorLog.Count > 0)
{
    Console.WriteLine($"  ERRORS ({errorLog.Count}):");
    foreach (var e in errorLog)
        Console.WriteLine($"  ! {e}");
    Console.WriteLine();
}
else
{
    Console.WriteLine("  ERRORS: none");
    Console.WriteLine();
}

Console.WriteLine($"[kyv-recorder] processed {processedCount}/{allPdfs.Count} PDF(s)");
if (!flags.Verify)
    Console.WriteLine($"[kyv-recorder] cassette  → {cassette}");
Console.WriteLine($"[kyv-recorder] texts     → {textsRoot}");

return errorLog.Count > 0 ? 1 : 0;

// ── Helpers ────────────────────────────────────────────────────────────────────

static (string? Workspace, string? Cassette, string? TextsDir, bool Verify) ParseFlags(string[] args)
{
    string? workspace = null, cassette = null, textsDir = null;
    bool verify = false;
    for (int i = 0; i < args.Length; i++)
    {
        switch (args[i].ToLowerInvariant())
        {
            case "--workspace": if (i + 1 < args.Length) workspace = args[++i]; break;
            case "--cassette":  if (i + 1 < args.Length) cassette  = args[++i]; break;
            case "--texts-dir": if (i + 1 < args.Length) textsDir  = args[++i]; break;
            case "--verify":    verify = true; break;
        }
    }
    return (workspace, cassette, textsDir, verify);
}

static string FindRepoRoot()
{
    var dir = AppContext.BaseDirectory;
    while (!string.IsNullOrEmpty(dir))
    {
        if (File.Exists(Path.Combine(dir, "Kozmo.sln"))) return dir;
        dir = Path.GetDirectoryName(dir);
    }
    throw new InvalidOperationException("Cannot locate repo root (Kozmo.sln not found).");
}

static void PrintUsage() => Console.WriteLine("""

    Kyv.CandidateRecorder usage:

      Record (requires OPENAI_API_KEY):
        OPENAI_API_KEY=sk-... dotnet run --project tools/Kyv.CandidateRecorder -- \
            --workspace "D:\June\Kozmo Workspace"

      Verify / replay (no API key):
        dotnet run --project tools/Kyv.CandidateRecorder -- \
            --workspace "D:\June\Kozmo Workspace" --verify

      Optional flags:
        --cassette <path>   Override cassette file path
        --texts-dir <path>  Override extracted-texts root directory
    """);

// Wraps IKozmoLlm and prints raw model JSON before returning — used in record mode
// so you can read exactly what the model returned before the post-filter runs.
sealed class LoggingLlm(IKozmoLlm inner) : IKozmoLlm
{
    public async Task<LlmResult> CompleteJsonAsync(
        string system, string user, int maxTokens = 500, CancellationToken ct = default)
    {
        var result = await inner.CompleteJsonAsync(system, user, maxTokens, ct);
        Console.WriteLine("  [raw model JSON]");
        Console.WriteLine("  " + (result.Answer is System.Text.Json.JsonElement el
            ? el.GetRawText()
            : "(null)"));
        Console.WriteLine();
        return result;
    }

    // Delegate vision calls so OCR recording works — the DIM throws NotSupportedException.
    public Task<LlmResult> CompleteVisionAsync(
        string system, byte[] imageBytes, int maxTokens = 500, CancellationToken ct = default)
        => inner.CompleteVisionAsync(system, imageBytes, maxTokens, ct);
}

record ScenarioResult(string Scenario, List<CandidateIdentityBelief> Beliefs);
