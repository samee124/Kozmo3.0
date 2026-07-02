// Kozmo.SeedPrep — two modes:
//
//   Signal recording (no args):
//     OPENAI_API_KEY=sk-... dotnet run --project tools/Kozmo.SeedPrep
//     Records LLM responses for all free-text signals into fixtures/llm-cache.json.
//
//   PDF extraction / cassette recording (pdf path as first arg):
//     dotnet run --project tools/Kozmo.SeedPrep -- contract.pdf
//         Extract page texts and print them (no API call).
//
//     dotnet run --project tools/Kozmo.SeedPrep -- contract.pdf --record --cassette fixtures/vendor-file/contract.cassette.json
//         Call OpenAI once, write beliefs + cassette. Requires OPENAI_API_KEY.
//
//     dotnet run --project tools/Kozmo.SeedPrep -- contract.pdf --verify --cassette fixtures/vendor-file/contract.cassette.json
//         Replay from cassette (no API call), print beliefs + locators.
//         Exits 1 if the cassette is missing or the entry is absent.

using System.Text.Json;
using Ii.Intake;
using Ii.Observation;
using Km.Store;
using Kozmo.Contracts;
using Kozmo.Contracts.Config;
using Kozmo.Llm;
using Kozmo.Llm.OpenAi;

// ── Mode dispatch ─────────────────────────────────────────────────────────────

// Effective date injected into every belief's observed_at.
// Matches DemoClock.AsOf so extracted beliefs are in-period for the demo.
var effectiveDate = new DateTimeOffset(2026, 6, 15, 0, 0, 0, TimeSpan.Zero);

if (args.Length == 0)
    return await RunSignalModeAsync();

var pdfPath = args[0];
if (pdfPath.StartsWith('-'))
{
    PrintUsage();
    return 1;
}

if (!File.Exists(pdfPath))
{
    Console.Error.WriteLine($"[seed-prep] file not found: {pdfPath}");
    return 1;
}

var flags = ParseFlags(args[1..]);

if (flags.Record && flags.Verify)
{
    Console.Error.WriteLine("[seed-prep] --record and --verify are mutually exclusive");
    return 1;
}
if ((flags.Record || flags.Verify) && flags.Cassette is null)
{
    Console.Error.WriteLine("[seed-prep] --cassette <path> is required with --record / --verify");
    return 1;
}

var pdfBytes = File.ReadAllBytes(pdfPath);

if (!flags.Record && !flags.Verify)
    return RunExtract(pdfBytes, pdfPath);

var repoRoot      = FindRepoRoot();
var cataloguePath = Path.Combine(repoRoot, "catalogue", "profiles", "saas");
var profile       = new Catalogue().Load(cataloguePath);

return flags.Record
    ? await RunRecordAsync(pdfBytes, pdfPath, flags.Cassette!, profile, effectiveDate)
    : await RunVerifyAsync(pdfBytes, pdfPath, flags.Cassette!, profile, effectiveDate);

// ── PDF mode: extract (no API) ────────────────────────────────────────────────

static int RunExtract(byte[] pdfBytes, string pdfPath)
{
    Console.WriteLine($"[seed-prep] extract — {pdfPath}");
    var pages = new PdfTextExtractor().ExtractPageTexts(pdfBytes);
    Console.WriteLine($"[seed-prep] {pages.Count} page(s)");
    Console.WriteLine(new string('─', 72));
    foreach (var (num, text) in pages.OrderBy(kv => kv.Key))
    {
        Console.WriteLine($"── Page {num} " + new string('─', 60));
        Console.WriteLine(text.Length > 0 ? text : "(empty)");
        Console.WriteLine();
    }
    return 0;
}

// ── PDF mode: record (calls OpenAI, writes cassette) ─────────────────────────

static async Task<int> RunRecordAsync(
    byte[] pdfBytes, string pdfPath, string cassettePath, SaasProfile profile,
    DateTimeOffset effectiveDate)
{
    if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENAI_API_KEY")))
    {
        Console.Error.WriteLine("[seed-prep] OPENAI_API_KEY is not set — cannot record");
        Console.Error.WriteLine("[seed-prep] set the env var and re-run");
        return 1;
    }

    Console.WriteLine($"[seed-prep] record        — {pdfPath}");
    Console.WriteLine($"[seed-prep] cassette      — {cassettePath}");
    Console.WriteLine($"[seed-prep] effectiveDate — {effectiveDate:yyyy-MM-dd}");

    IKozmoLlm inner;
    try { inner = new OpenAiLlmClient(); }
    catch (InvalidOperationException ex)
    {
        Console.Error.WriteLine($"[seed-prep] OpenAI client error: {ex.Message}");
        return 1;
    }

    var cachingLlm        = new CachingLlmClient(cassettePath, recordMode: true, inner: inner);
    var llm               = new LoggingLlm(cachingLlm);
    var (lane, ev, vendorId) = BuildLane(llm, pdfPath, profile, effectiveDate);

    Console.WriteLine("[seed-prep] calling OpenAI (this may take a few seconds)…");
    var beliefs = await lane.ExtractFromBytesAndWriteAsync(vendorId, ev, pdfBytes, effectiveDate);

    Console.WriteLine($"[seed-prep] {beliefs.Count} belief(s) survived catalogue filter:");
    PrintBeliefs(beliefs);
    Console.WriteLine($"[seed-prep] cassette → {cassettePath}");
    return 0;
}

// ── PDF mode: verify (replay from cassette, no API call) ─────────────────────

static async Task<int> RunVerifyAsync(
    byte[] pdfBytes, string pdfPath, string cassettePath, SaasProfile profile,
    DateTimeOffset effectiveDate)
{
    Console.WriteLine($"[seed-prep] verify   — {pdfPath}");
    Console.WriteLine($"[seed-prep] cassette — {cassettePath}");

    if (!File.Exists(cassettePath))
    {
        Console.Error.WriteLine($"[seed-prep] cassette not found — run --record first");
        return 1;
    }

    var llm               = new CachingLlmClient(cassettePath, recordMode: false);
    var (lane, ev, vendorId) = BuildLane(llm, pdfPath, profile, effectiveDate);

    try
    {
        var beliefs = await lane.ExtractFromBytesAndWriteAsync(vendorId, ev, pdfBytes, effectiveDate);
        Console.WriteLine($"[seed-prep] cassette HIT — {beliefs.Count} belief(s) replayed:");
        PrintBeliefs(beliefs);
        return 0;
    }
    catch (LlmCacheMissException)
    {
        Console.Error.WriteLine("[seed-prep] cassette MISS — no matching entry found");
        Console.Error.WriteLine("[seed-prep] run with --record to populate the cassette first");
        return 1;
    }
}

// ── Signal mode (existing behaviour — no args) ────────────────────────────────

static Task<int> RunSignalModeAsync()
{
    var repoRoot      = FindRepoRoot();
    var signalsPath   = Path.Combine(repoRoot, "fixtures", "signals.json");
    var cachePath     = Path.Combine(repoRoot, "fixtures", "llm-cache.json");
    var cataloguePath = Path.Combine(repoRoot, "catalogue", "profiles", "saas");

    Console.WriteLine($"[seed-prep] signals  : {signalsPath}");
    Console.WriteLine($"[seed-prep] cache    : {cachePath}");

    var inner   = new OpenAiLlmClient();
    var cacher  = new CachingLlmClient(cachePath, recordMode: true, inner: inner);
    var obs     = new ObservationModule(cacher);
    var profile = new Catalogue().Load(cataloguePath);
    var signals = LoadSignals(signalsPath);

    Console.WriteLine($"[seed-prep] {signals.Count} signal(s) to classify");

    int recorded = 0;
    foreach (var sig in signals)
    {
        Console.Write($"  → {sig.ExternalId} ... ");
        var result = obs.Classify(sig, profile);
        if (result is null) { Console.WriteLine("skipped"); continue; }
        var isLlm = result.Derivation.StartsWith("llm:", StringComparison.OrdinalIgnoreCase);
        Console.WriteLine($"OK [{(isLlm ? "llm" : "rule")}] ({result.Dimension}/{result.Criterion} = {result.Value:F2})");
        if (isLlm) recorded++;
    }

    Console.WriteLine($"[seed-prep] done — {recorded} result(s) written to {cachePath}");
    return Task.FromResult(0);
}

// ── Shared helpers ────────────────────────────────────────────────────────────

static (VendorFilePdfLane lane, Evidence ev, Guid vendorId) BuildLane(
    IKozmoLlm llm, string pdfPath, SaasProfile profile, DateTimeOffset effectiveDate)
{
    var vendorId = Guid.NewGuid();
    var store    = new SqliteEntityStore("Data Source=:memory:", profile);
    var svc      = new VendorFileWriteService(store, profile);
    var lane     = new VendorFilePdfLane(llm, svc, profile);
    var ev       = new Evidence(
        EvidenceId: Guid.NewGuid(),
        VendorId:   vendorId,
        DocType:    DocType.SignedContract,
        SourceTier: SourceTier.Primary,
        Ref:        Path.GetFileName(pdfPath),
        DocVersion: 1,
        IngestedAt: effectiveDate);
    return (lane, ev, vendorId);
}

static void PrintBeliefs(IReadOnlyList<Belief> beliefs)
{
    if (beliefs.Count == 0) { Console.WriteLine("  (no beliefs matched the catalogue)"); return; }
    foreach (var b in beliefs)
    {
        var locator = b.Provenance?.Locator ?? "(no locator)";
        Console.WriteLine(
            $"  {b.ClaimKey,-22}  val={b.Value,14:G}  conf={b.Confidence:F2}" +
            $"  tier={b.SourceTier,-10}  {locator}");
    }
}

static (bool Record, bool Verify, string? Cassette) ParseFlags(string[] args)
{
    bool record  = args.Contains("--record",  StringComparer.OrdinalIgnoreCase);
    bool verify  = args.Contains("--verify",  StringComparer.OrdinalIgnoreCase);
    string? cassette = null;
    for (int i = 0; i < args.Length - 1; i++)
    {
        if (string.Equals(args[i], "--cassette", StringComparison.OrdinalIgnoreCase))
        {
            cassette = args[i + 1];
            break;
        }
    }
    return (record, verify, cassette);
}

static void PrintUsage()
{
    Console.WriteLine("""
        Kozmo.SeedPrep usage:

          dotnet run --project tools/Kozmo.SeedPrep
              Record LLM responses for all signals (requires OPENAI_API_KEY).

          dotnet run --project tools/Kozmo.SeedPrep -- <pdf>
              Extract page texts and print them (no API call).

          dotnet run --project tools/Kozmo.SeedPrep -- <pdf> --record --cassette <path>
              Call OpenAI, extract beliefs, write cassette (requires OPENAI_API_KEY).

          dotnet run --project tools/Kozmo.SeedPrep -- <pdf> --verify --cassette <path>
              Replay cassette, print beliefs + locators. Exit 1 on cache miss.
        """);
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

static List<Signal> LoadSignals(string path)
{
    using var doc = JsonDocument.Parse(File.ReadAllText(path));
    return doc.RootElement.EnumerateArray().Select(ParseSignal).ToList();
}

static Signal ParseSignal(JsonElement e) =>
    new Signal(
        Id:           Guid.Parse(e.GetProperty("id").GetString()!),
        EntityId:     Guid.Parse(e.GetProperty("entity_id").GetString()!),
        CustomerId:   Guid.Parse(e.GetProperty("customer_id").GetString()!),
        SourceSystem: Enum.Parse<SourceSystem>(e.GetProperty("source_system").GetString()!, ignoreCase: true),
        ExternalId:   e.GetProperty("external_id").GetString()!,
        Payload:      ParsePayload(e.GetProperty("payload")),
        ObservedAt:   DateTimeOffset.Parse(e.GetProperty("observed_at").GetString()!),
        ReceivedAt:   DateTimeOffset.Parse(e.GetProperty("received_at").GetString()!),
        TraceId:      Guid.Parse(e.GetProperty("trace_id").GetString()!));

static IReadOnlyDictionary<string, object?> ParsePayload(JsonElement payload)
{
    var dict = new Dictionary<string, object?>();
    foreach (var prop in payload.EnumerateObject())
    {
        dict[prop.Name] = prop.Value.ValueKind switch
        {
            JsonValueKind.Number => prop.Value.GetDouble(),
            JsonValueKind.String => (object?)prop.Value.GetString(),
            JsonValueKind.True   => (object?)true,
            JsonValueKind.False  => (object?)false,
            _                    => null
        };
    }
    return dict;
}

// Wraps any IKozmoLlm and prints the raw model JSON to stdout before returning it.
// Used in --record mode so you can see what claims the model extracted BEFORE
// VendorFilePdfLane's catalogue filter silently drops unknown claim_keys.
sealed class LoggingLlm(IKozmoLlm inner) : IKozmoLlm
{
    public async Task<LlmResult> CompleteJsonAsync(
        string system, string user, int maxTokens = 500, CancellationToken ct = default)
    {
        var result = await inner.CompleteJsonAsync(system, user, maxTokens, ct);
        Console.WriteLine("[seed-prep] raw model JSON (before catalogue filter):");
        Console.WriteLine(result.Answer is System.Text.Json.JsonElement el
            ? el.GetRawText()
            : "(null)");
        Console.WriteLine();
        return result;
    }
}
