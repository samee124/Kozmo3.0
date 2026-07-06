// Kyv.EmailCandidateRecorder — record or verify email identity-extraction cassette entries
// (E-signal Part 5 Step 6)
//
// Writes into the SAME candidate-extraction.cassette.json PDFs use (spec §2.4 Decision 3: email
// is ingested through the existing DocumentCandidateExtractor/ClusteringStage path for identity
// resolution only — not a new mechanism, not a new cassette).
//
// RECORD (requires OPENAI_API_KEY):
//   OPENAI_API_KEY=sk-... dotnet run --project tools/Kyv.EmailCandidateRecorder
//
// VERIFY (no API key — replays from cassette):
//   dotnet run --project tools/Kyv.EmailCandidateRecorder -- --verify
//
// Optional flags:
//   --cassette <path>   Override cassette file (default: fixtures/kyv/candidate-extraction.cassette.json)
//   --workspace <path>  Override workspace root (default: D:\June\Kozmo Workspace)

using Ig.Contracts;
using Ii.CandidateExtraction;
using Kozmo.Contracts;
using Kozmo.Llm;
using Kozmo.Llm.OpenAi;

var flags     = ParseFlags(args);
var repoRoot  = FindRepoRoot();
var cassette  = flags.Cassette  ?? Path.Combine(repoRoot, "fixtures", "kyv", "candidate-extraction.cassette.json");
var workspace = flags.Workspace ?? @"D:\June\Kozmo Workspace";

Directory.CreateDirectory(Path.GetDirectoryName(cassette)!);

if (!Directory.Exists(workspace))
{
    Console.Error.WriteLine($"[email-candidate-recorder] workspace not found: {workspace}");
    return 1;
}

var allEmails = Directory.EnumerateFiles(workspace, "*.eml", SearchOption.AllDirectories)
    .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
    .ToList();

if (allEmails.Count == 0)
{
    Console.Error.WriteLine($"[email-candidate-recorder] no .eml files found (recursive) in: {workspace}");
    return 1;
}

Console.WriteLine($"[email-candidate-recorder] workspace : {workspace}");
Console.WriteLine($"[email-candidate-recorder] cassette  : {cassette}");
Console.WriteLine($"[email-candidate-recorder] mode      : {(flags.Verify ? "verify (replay)" : "record")}");
Console.WriteLine($"[email-candidate-recorder] {allEmails.Count} .eml file(s)");
Console.WriteLine();

if (!flags.Verify && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENAI_API_KEY")))
{
    Console.Error.WriteLine("[email-candidate-recorder] OPENAI_API_KEY is not set — cannot record");
    Console.Error.WriteLine("[email-candidate-recorder] set the env var and re-run, or use --verify to replay");
    return 1;
}

IKozmoLlm baseLlm;
if (flags.Verify)
{
    if (!File.Exists(cassette))
    {
        Console.Error.WriteLine("[email-candidate-recorder] cassette not found — run without --verify first");
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
        Console.Error.WriteLine($"[email-candidate-recorder] OpenAI client error: {ex.Message}");
        return 1;
    }
    baseLlm = new CachingLlmClient(cassette, recordMode: true, inner: inner);
}

var extractor    = new DocumentCandidateExtractor(baseLlm);
var errorLog     = new List<string>();
int processed    = 0;
int totalParties = 0;

foreach (var path in allEmails)
{
    var relPath = Path.GetRelativePath(workspace, path);
    var fileName = Path.GetFileName(path);
    Console.WriteLine($"── {relPath}");

    ParsedEmail email;
    try
    {
        email = EmailParser.ParseFile(path);
    }
    catch (Exception ex)
    {
        var msg = $"[ERROR] parse failed: {ex.Message}  →  {relPath}";
        Console.Error.WriteLine($"  {msg}");
        errorLog.Add(msg);
        Console.WriteLine();
        continue;
    }

    var identityText = EmailParser.BuildIdentityText(email);

    IReadOnlyList<CandidateIdentityBelief> parties;
    try
    {
        // Correspondence tier always — every email is correspondence, never filename-inferred
        // the way a document's tier is (DocTypeInferrer.InferTier's heuristics don't apply here).
        parties = await extractor.ExtractAsync(identityText, fileName, SourceTier.Correspondence);
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

    if (parties.Count == 0)
    {
        Console.WriteLine("  (no candidates after filter)");
    }
    else
    {
        foreach (var p in parties)
            Console.WriteLine(
                $"  + {p.RawName,-40}  role={p.RoleHint ?? "unknown",-10}  " +
                $"domain={p.Signals?.Domain ?? "(none)",-28}  conf={p.Confidence:F2}");
    }

    totalParties += parties.Count;
    processed++;
    Console.WriteLine();
}

Console.WriteLine(new string('═', 72));
Console.WriteLine($"[email-candidate-recorder] processed {processed}/{allEmails.Count} email(s), {totalParties} candidate(s) total");
if (!flags.Verify)
    Console.WriteLine($"[email-candidate-recorder] cassette → {cassette}");

if (errorLog.Count > 0)
{
    Console.WriteLine($"[email-candidate-recorder] ERRORS ({errorLog.Count}):");
    foreach (var e in errorLog)
        Console.WriteLine($"  ! {e}");
}
else
{
    Console.WriteLine("[email-candidate-recorder] ERRORS: none");
}

return errorLog.Count > 0 ? 1 : 0;

// ── Helpers ────────────────────────────────────────────────────────────────────

static (string? Cassette, string? Workspace, bool Verify) ParseFlags(string[] args)
{
    string? cassette = null, workspace = null;
    bool verify = false;
    for (int i = 0; i < args.Length; i++)
    {
        switch (args[i].ToLowerInvariant())
        {
            case "--cassette":  if (i + 1 < args.Length) cassette  = args[++i]; break;
            case "--workspace": if (i + 1 < args.Length) workspace = args[++i]; break;
            case "--verify":    verify = true; break;
        }
    }
    return (cassette, workspace, verify);
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
