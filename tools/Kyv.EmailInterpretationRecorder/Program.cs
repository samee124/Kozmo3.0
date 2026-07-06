// Kyv.EmailInterpretationRecorder — record or verify email interpretation cassettes
// (E-signal Part 5 Step 5)
//
// SAMPLE (requires OPENAI_API_KEY) — the ~13-email Step 5 review gate. Run this FIRST; do not
// record the full 338 until the sample's belief-vs-signal routing has been reviewed:
//   OPENAI_API_KEY=sk-... dotnet run --project tools/Kyv.EmailInterpretationRecorder -- --sample
//
// FIX-VERIFY (requires OPENAI_API_KEY) — the small targeted set for confirming Fix 3/Fix 4 (the
// invoice-issuance-cadence and multi-invoice-total guards) without re-recording the full 338:
//   OPENAI_API_KEY=sk-... dotnet run --project tools/Kyv.EmailInterpretationRecorder -- --fix-verify
//
// FULL (requires OPENAI_API_KEY) — all 338 real .eml files. Only after the sample review passes:
//   OPENAI_API_KEY=sk-... dotnet run --project tools/Kyv.EmailInterpretationRecorder
//
// VERIFY (no API key — replays from cassette):
//   dotnet run --project tools/Kyv.EmailInterpretationRecorder -- --verify [--sample|--fix-verify]
//
// Optional flags:
//   --cassette <path>    Override cassette file (default: fixtures/kyv/email-interpretation.cassette.json)
//   --workspace <path>   Override workspace root (default: D:\June\Kozmo Workspace)

using Ii.CandidateExtraction;
using Km.Store;
using Kozmo.Llm;
using Kozmo.Llm.OpenAi;

// The curated Step 5 review sample — spans the two risky boundaries the review specifically
// tests (commitment-vs-belief on payment terms; dollar-figure guards on informal text) plus
// abstain cases (routine thanks/closing emails) and signal-only cases (escalation, handoff,
// renewal discussion). Hand-picked from a full keyword survey of all 338 real DECODED email
// bodies (raw-file grep misses the ~40% that are base64-encoded) — see the Part 5 Step 4/5 commits.
string[] sampleFiles =
[
    @"Scenario 01 — Golden Vendor\Emails\01_MSA_Execution_Confirmation_Apr2022.eml",
    @"Scenario 01 — Golden Vendor\Emails\03_Invoice_IIVS-INV-2022-0001_Jul2022.eml",
    @"Scenario 01 — Golden Vendor\Emails\05_SOW02_Initiation_PO_Confirmation_Mar2023.eml",
    @"Scenario 03 — Scattered Evidence\Emails (vendor name in email signature only)\03_Invoice_Query_Jul2022.eml",
    @"Scenario 04 — Conflicting Information\Emails\03_Invoice_Dispute_Aug2021.eml",
    @"Scenario 07 — Email-Driven Relationship\300 .eml files spanning 3 years\0002_demo_followup_intro_james.eml",
    @"Scenario 07 — Email-Driven Relationship\300 .eml files spanning 3 years\0006_pricing.eml",
    @"Scenario 07 — Email-Driven Relationship\300 .eml files spanning 3 years\0023_payment_0.eml",
    @"Scenario 07 — Email-Driven Relationship\300 .eml files spanning 3 years\0153_handoff.eml",
    @"Scenario 07 — Email-Driven Relationship\300 .eml files spanning 3 years\0186_escalation_19_5_4.eml",
    @"Scenario 07 — Email-Driven Relationship\300 .eml files spanning 3 years\0266_renewal.eml",
    @"Scenario 05 — Missing Financial Data\emails\09_security_review_closed.eml",
    @"Scenario 05 — Missing Financial Data\emails\18_contact_update.eml",
];

// The Fix 3/Fix 4 targeted verification set — the 2 emails the full-338 audit flagged, plus
// must-still-pass spot checks (clean payment_terms, settled annual_value, real single-invoice
// invoice_amount, renewal_date) — confirms both guards without re-recording the full corpus.
string[] fixVerifyFiles =
[
    @"Scenario 04 — Conflicting Information\Emails\01_Contract_Kickoff_Mar2021.eml",
    @"Scenario 03 — Scattered Evidence\Emails (vendor name in email signature only)\05_Year_End_Review_Dec2022.eml",
    @"Scenario 01 — Golden Vendor\Emails\01_MSA_Execution_Confirmation_Apr2022.eml",
    @"Scenario 01 — Golden Vendor\Emails\03_Invoice_IIVS-INV-2022-0001_Jul2022.eml",
    @"Scenario 03 — Scattered Evidence\Emails (vendor name in email signature only)\03_Invoice_Query_Jul2022.eml",
    @"Scenario 01 — Golden Vendor\Emails\05_SOW02_Initiation_PO_Confirmation_Mar2023.eml",
    @"Scenario 01 — Golden Vendor\Emails\08_MSA_Auto_Renewal_Year3_Apr2024.eml",
    @"Scenario 07 — Email-Driven Relationship\300 .eml files spanning 3 years\0023_payment_0.eml",
    @"Scenario 07 — Email-Driven Relationship\300 .eml files spanning 3 years\0034_payment_1.eml",
    @"Scenario 07 — Email-Driven Relationship\300 .eml files spanning 3 years\0043_payment_2.eml",
    @"Scenario 07 — Email-Driven Relationship\300 .eml files spanning 3 years\0077_payment_5.eml",
];

var flags        = ParseFlags(args);
var repoRoot     = FindRepoRoot();
var cassette     = flags.Cassette  ?? Path.Combine(repoRoot, "fixtures", "kyv", "email-interpretation.cassette.json");
var workspace    = flags.Workspace ?? @"D:\June\Kozmo Workspace";
var catalogueDir = Path.Combine(repoRoot, "catalogue", "profiles", "saas");

Directory.CreateDirectory(Path.GetDirectoryName(cassette)!);

if (!Directory.Exists(workspace))
{
    Console.Error.WriteLine($"[email-interpretation-recorder] workspace not found: {workspace}");
    return 1;
}

var profile = new Catalogue().Load(catalogueDir);

var allFiles = flags.FixVerify
    ? fixVerifyFiles.Select(rel => Path.Combine(workspace, rel)).ToList()
    : flags.Sample
        ? sampleFiles.Select(rel => Path.Combine(workspace, rel)).ToList()
        : Directory.EnumerateFiles(workspace, "*.eml", SearchOption.AllDirectories)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();

var missing = allFiles.Where(p => !File.Exists(p)).ToList();
if (missing.Count > 0)
{
    Console.Error.WriteLine($"[email-interpretation-recorder] {missing.Count} sample file(s) not found:");
    foreach (var m in missing) Console.Error.WriteLine($"  {m}");
    return 1;
}

Console.WriteLine($"[email-interpretation-recorder] workspace : {workspace}");
Console.WriteLine($"[email-interpretation-recorder] cassette  : {cassette}");
Console.WriteLine($"[email-interpretation-recorder] mode      : {(flags.Verify ? "verify (replay)" : "record")}");
var scopeLabel = flags.FixVerify ? "FIX-VERIFY" : flags.Sample ? "SAMPLE" : "FULL";
Console.WriteLine($"[email-interpretation-recorder] scope     : {scopeLabel} ({allFiles.Count})");
Console.WriteLine();

if (!flags.Verify && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENAI_API_KEY")))
{
    Console.Error.WriteLine("[email-interpretation-recorder] OPENAI_API_KEY is not set — cannot record");
    Console.Error.WriteLine("[email-interpretation-recorder] set the env var and re-run, or use --verify to replay");
    return 1;
}

IKozmoLlm baseLlm;
if (flags.Verify)
{
    if (!File.Exists(cassette))
    {
        Console.Error.WriteLine("[email-interpretation-recorder] cassette not found — run without --verify first");
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
        Console.Error.WriteLine($"[email-interpretation-recorder] OpenAI client error: {ex.Message}");
        return 1;
    }
    baseLlm = new CachingLlmClient(cassette, recordMode: true, inner: inner);
}

var extractor  = new EmailInterpretationExtractor(baseLlm, profile);
var errorLog   = new List<string>();
int processed  = 0;
int totalBeliefs = 0;
int totalSignals = 0;

foreach (var path in allFiles)
{
    var relPath = Path.GetRelativePath(workspace, path);
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

    Console.WriteLine($"  from={email.From?.Address} date={email.Date:yyyy-MM-dd} subject=\"{email.Subject}\"");

    IReadOnlyList<BeliefCandidate>   beliefs;
    IReadOnlyList<MetadataCandidate> signals;
    try
    {
        var extraction = await extractor.ExtractAsync(email);
        beliefs = extraction.Beliefs;
        signals = extraction.Metadata;
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

    if (beliefs.Count == 0)
        Console.WriteLine("  (no beliefs)");
    else
        foreach (var b in beliefs)
            Console.WriteLine(
                $"  BELIEF + {b.Criterion,-16} value={b.Value,-14} tier={b.SourceTier} conf={b.Confidence:F2}" +
                $"\n           derivation: {b.Derivation}");

    if (signals.Count == 0)
        Console.WriteLine("  (no signals)");
    else
        foreach (var s in signals)
            Console.WriteLine(
                $"  SIGNAL  + {s.FieldName,-20} value={s.Value}" +
                $"\n           derivation: {s.Derivation}");

    totalBeliefs += beliefs.Count;
    totalSignals += signals.Count;
    processed++;
    Console.WriteLine();
}

Console.WriteLine(new string('═', 72));
Console.WriteLine($"[email-interpretation-recorder] processed {processed}/{allFiles.Count} email(s), " +
                   $"{totalBeliefs} belief(s), {totalSignals} signal(s) total");
if (!flags.Verify)
    Console.WriteLine($"[email-interpretation-recorder] cassette → {cassette}");

if (errorLog.Count > 0)
{
    Console.WriteLine($"[email-interpretation-recorder] ERRORS ({errorLog.Count}):");
    foreach (var e in errorLog)
        Console.WriteLine($"  ! {e}");
}
else
{
    Console.WriteLine("[email-interpretation-recorder] ERRORS: none");
}

return errorLog.Count > 0 ? 1 : 0;

// ── Helpers ────────────────────────────────────────────────────────────────────

static (string? Cassette, string? Workspace, bool Verify, bool Sample, bool FixVerify) ParseFlags(string[] args)
{
    string? cassette = null, workspace = null;
    bool verify = false, sample = false, fixVerify = false;
    for (int i = 0; i < args.Length; i++)
    {
        switch (args[i].ToLowerInvariant())
        {
            case "--cassette":   if (i + 1 < args.Length) cassette  = args[++i]; break;
            case "--workspace":  if (i + 1 < args.Length) workspace = args[++i]; break;
            case "--verify":     verify = true; break;
            case "--sample":     sample = true; break;
            case "--fix-verify": fixVerify = true; break;
        }
    }
    return (cassette, workspace, verify, sample, fixVerify);
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
