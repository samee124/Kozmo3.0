// Kozmo.SeedPrep — records LLM responses for all free-text signals into fixtures/llm-cache.json.
// Run once with a real API key; commit the resulting cache file for deterministic demo replay.
//
// Usage:
//   OPENAI_API_KEY=sk-... dotnet run --project tools/Kozmo.SeedPrep
//
// The tool exits with code 1 if OPENAI_API_KEY is absent.

using System.Text.Json;
using Ii.Observation;
using Km.Store;
using Kozmo.Contracts;
using Kozmo.Llm;
using Kozmo.Llm.OpenAi;

var repoRoot = FindRepoRoot();
var signalsPath   = Path.Combine(repoRoot, "fixtures", "signals.json");
var cachePath     = Path.Combine(repoRoot, "fixtures", "llm-cache.json");
var cataloguePath = Path.Combine(repoRoot, "catalogue", "profiles", "saas");

Console.WriteLine($"[seed-prep] signals : {signalsPath}");
Console.WriteLine($"[seed-prep] cache   : {cachePath}");

// ── Build the recording stack ──────────────────────────────────────────────

var inner  = new OpenAiLlmClient();          // real OpenAI client (reads OPENAI_API_KEY)
var cacher = new CachingLlmClient(cachePath, recordMode: true, inner: inner);
var obs    = new ObservationModule(cacher);
var profile = new Catalogue().Load(cataloguePath);

// ── Run all signals through classify; LLM is invoked for any that skip rules ──

var allSignals = LoadSignals(signalsPath);

Console.WriteLine($"[seed-prep] {allSignals.Count} signal(s) to classify (LLM called for rule misses)");

int recorded = 0;
foreach (var sig in allSignals)
{
    Console.Write($"  → signal {sig.ExternalId} ... ");
    var result = obs.Classify(sig, profile);
    if (result is null)
    {
        Console.WriteLine("skipped (no classification)");
        continue;
    }
    var isLlm = result.Derivation.StartsWith("llm:", StringComparison.OrdinalIgnoreCase);
    Console.WriteLine($"OK [{(isLlm ? "llm" : "rule")}] ({result.Dimension}/{result.Criterion} = {result.Value:F2})");
    if (isLlm) recorded++;
}

Console.WriteLine($"[seed-prep] done — {recorded} result(s) written to {cachePath}");
return 0;

// ── Helpers ───────────────────────────────────────────────────────────────

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
    return doc.RootElement.EnumerateArray()
        .Select(ParseSignal)
        .ToList();
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
            _                   => null
        };
    }
    return dict;
}
