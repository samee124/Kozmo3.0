// Kyv.BeliefRecorder — record or verify belief-extraction cassettes
//
// RECORD (requires OPENAI_API_KEY):
//   OPENAI_API_KEY=sk-... dotnet run --project tools/Kyv.BeliefRecorder
//
//   Walks every .txt file already committed under fixtures/kyv/texts/ (no PDF workspace
//   needed — text extraction already happened for the candidate-extraction recorder).
//   For each file: calls OpenAI once via DocumentBeliefExtractor, writes a cassette entry.
//
// VERIFY (no API key — replays from cassette):
//   dotnet run --project tools/Kyv.BeliefRecorder -- --verify
//
// Optional flags:
//   --cassette <path>   Override cassette file (default: fixtures/kyv/belief-extraction.cassette.json)
//   --texts-dir <path>  Override texts root (default: fixtures/kyv/texts)

using Ii.CandidateExtraction;
using Km.Store;
using Kozmo.Llm;
using Kozmo.Llm.OpenAi;

var flags = ParseFlags(args);

var repoRoot      = FindRepoRoot();
var cassette      = flags.Cassette ?? Path.Combine(repoRoot, "fixtures", "kyv", "belief-extraction.cassette.json");
var textsRoot     = flags.TextsDir ?? Path.Combine(repoRoot, "fixtures", "kyv", "texts");
var cataloguePath = Path.Combine(repoRoot, "catalogue", "profiles", "saas");

Directory.CreateDirectory(Path.GetDirectoryName(cassette)!);

if (!Directory.Exists(textsRoot))
{
    Console.Error.WriteLine($"[belief-recorder] texts dir not found: {textsRoot}");
    return 1;
}

var profile = new Catalogue().Load(cataloguePath);

var allTexts = Directory.EnumerateFiles(textsRoot, "*.txt", SearchOption.AllDirectories)
    .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
    .ToList();

if (allTexts.Count == 0)
{
    Console.Error.WriteLine($"[belief-recorder] no .txt files found under: {textsRoot}");
    return 1;
}

Console.WriteLine($"[belief-recorder] texts-root : {textsRoot}");
Console.WriteLine($"[belief-recorder] cassette   : {cassette}");
Console.WriteLine($"[belief-recorder] mode       : {(flags.Verify ? "verify (replay)" : "record")}");
Console.WriteLine($"[belief-recorder] {allTexts.Count} text file(s)");
Console.WriteLine();

if (!flags.Verify && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENAI_API_KEY")))
{
    Console.Error.WriteLine("[belief-recorder] OPENAI_API_KEY is not set — cannot record");
    Console.Error.WriteLine("[belief-recorder] set the env var and re-run, or use --verify to replay");
    return 1;
}

IKozmoLlm baseLlm;
if (flags.Verify)
{
    if (!File.Exists(cassette))
    {
        Console.Error.WriteLine("[belief-recorder] cassette not found — run without --verify first");
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
        Console.Error.WriteLine($"[belief-recorder] OpenAI client error: {ex.Message}");
        return 1;
    }
    var cachingLlm = new CachingLlmClient(cassette, recordMode: true, inner: inner);
    baseLlm = new LoggingLlm(cachingLlm); // prints raw JSON before the filter
}

var extractor     = new DocumentBeliefExtractor(baseLlm, profile);
var errorLog      = new List<string>();
int processed     = 0;
int totalFacts    = 0;
int totalMetadata = 0;

foreach (var textFile in allTexts)
{
    var fileName = Path.GetFileName(textFile);
    var relPath  = Path.GetRelativePath(textsRoot, textFile);
    var tier     = DocTypeInferrer.InferTier(fileName);
    var text     = File.ReadAllText(textFile);

    Console.WriteLine($"  ── {relPath}  [{tier}]");

    IReadOnlyList<BeliefCandidate>   facts;
    IReadOnlyList<MetadataCandidate> metadata;
    try
    {
        var extraction = await extractor.ExtractAsync(text, fileName, tier);
        facts    = extraction.Beliefs;
        metadata = extraction.Metadata;
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

    if (facts.Count == 0)
    {
        Console.WriteLine("  (no facts)");
    }
    else
    {
        foreach (var f in facts)
            Console.WriteLine(
                $"  + {f.Criterion,-16} value={f.Value,-14} conf={f.Confidence:F2}" +
                $"  dim={(f.Dimension.HasValue ? f.Dimension.ToString() : "(none)")}");
    }

    if (metadata.Count > 0)
    {
        foreach (var m in metadata)
            Console.WriteLine($"  * {m.FieldName,-24} value={m.Value}");
    }

    totalFacts    += facts.Count;
    totalMetadata += metadata.Count;
    processed++;
    Console.WriteLine();
}

Console.WriteLine(new string('═', 72));
Console.WriteLine($"[belief-recorder] processed {processed}/{allTexts.Count} file(s), {totalFacts} fact(s), {totalMetadata} metadata field(s) total");
if (!flags.Verify)
    Console.WriteLine($"[belief-recorder] cassette → {cassette}");

if (errorLog.Count > 0)
{
    Console.WriteLine($"[belief-recorder] ERRORS ({errorLog.Count}):");
    foreach (var e in errorLog)
        Console.WriteLine($"  ! {e}");
}
else
{
    Console.WriteLine("[belief-recorder] ERRORS: none");
}

return errorLog.Count > 0 ? 1 : 0;

// ── Helpers ────────────────────────────────────────────────────────────────────

static (string? Cassette, string? TextsDir, bool Verify) ParseFlags(string[] args)
{
    string? cassette = null, textsDir = null;
    bool verify = false;
    for (int i = 0; i < args.Length; i++)
    {
        switch (args[i].ToLowerInvariant())
        {
            case "--cassette":  if (i + 1 < args.Length) cassette = args[++i]; break;
            case "--texts-dir": if (i + 1 < args.Length) textsDir = args[++i]; break;
            case "--verify":    verify = true; break;
        }
    }
    return (cassette, textsDir, verify);
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

// Wraps IKozmoLlm and prints raw model JSON before returning — used in record mode so you can
// read exactly what the model returned before the post-filter runs.
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

    public Task<LlmResult> CompleteVisionAsync(
        string system, byte[] imageBytes, int maxTokens = 500, CancellationToken ct = default)
        => inner.CompleteVisionAsync(system, imageBytes, maxTokens, ct);
}
