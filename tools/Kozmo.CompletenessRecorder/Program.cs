// Kozmo.CompletenessRecorder — record answering-stage cassette for completeness tests
//
// RECORD (requires OPENAI_API_KEY):
//   $env:OPENAI_API_KEY="sk-..."
//   dotnet run --project tools/Kozmo.CompletenessRecorder
//
//   Runs QuestionAnsweringStage in record mode for IIVS (rich) and Regulus (sparse)
//   fixture vendors at L1 depth. Writes cassette entries to:
//     fixtures/completeness/answering.cassette.json
//
// VERIFY (no API key — replays from cassette):
//   dotnet run --project tools/Kozmo.CompletenessRecorder -- --verify
//
// Optional flags:
//   --cassette <path>   Override cassette file (default: fixtures/completeness/answering.cassette.json)
//   --depth <L1|L2|L3>  Max depth to select questions (default: L1)
//
// SYNC CONTRACT: the fixture beliefs defined below MUST be byte-identical to those in
// subsystems/interpretation-inference/dotnet/Ii.Completeness.Tests/FixtureBeliefs.cs.
// Any field difference changes the AnsweringPrompt.User string → different cassette key →
// LlmCacheMissException when the integration tests replay → obvious failure.

using Ii.Completeness;
using Kozmo.Contracts;
using Kozmo.Llm;
using Kozmo.Llm.OpenAi;

var flags    = ParseFlags(args);
var repoRoot = FindRepoRoot();
var cassette = flags.Cassette ?? Path.Combine(repoRoot, "fixtures", "completeness", "answering.cassette.json");
var maxDepth = flags.Depth   ?? DepthLevel.L1;
var anchorNow = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);

Directory.CreateDirectory(Path.GetDirectoryName(cassette)!);

Console.WriteLine($"[completeness-recorder] cassette : {cassette}");
Console.WriteLine($"[completeness-recorder] mode     : {(flags.Verify ? "verify (replay)" : "record")}");
Console.WriteLine($"[completeness-recorder] depth    : {maxDepth}");
Console.WriteLine();

if (!flags.Verify && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENAI_API_KEY")))
{
    Console.Error.WriteLine("[completeness-recorder] OPENAI_API_KEY is not set — cannot record");
    Console.Error.WriteLine("[completeness-recorder] set the env var and re-run, or use --verify to replay");
    return 1;
}

IKozmoLlm llm;
if (flags.Verify)
{
    if (!File.Exists(cassette))
    {
        Console.Error.WriteLine($"[completeness-recorder] cassette not found — run without --verify first");
        return 1;
    }
    llm = new CachingLlmClient(cassette, recordMode: false);
}
else
{
    IKozmoLlm inner;
    try { inner = new OpenAiLlmClient(); }
    catch (InvalidOperationException ex)
    {
        Console.Error.WriteLine($"[completeness-recorder] OpenAI client error: {ex.Message}");
        return 1;
    }
    llm = new LoggingLlm(new CachingLlmClient(cassette, recordMode: true, inner: inner));
}

var stage     = new QuestionAnsweringStage(llm);
var questions = QuestionSelector.Select(SaasQuestionBank.Category, maxDepth);
var errors    = 0;

Console.WriteLine($"Questions selected: {questions.Count} (category={SaasQuestionBank.Category}, maxDepth={maxDepth})");
Console.WriteLine();

// ── SYNC CONTRACT: beliefs below must be byte-identical to FixtureBeliefs.cs ─
// If any field differs the cassette key changes → LlmCacheMissException in tests.

var iivsVendorId    = Guid.Parse("3fa85f64-5717-4562-b3fc-2c963f66afa6");
var regulusVendorId = Guid.Parse("7c9e6679-7425-40de-944b-e07fc1f90ae7");
var anchor          = new DateTimeOffset(2024, 11, 15, 0, 0, 0, TimeSpan.Zero);
var trace1          = Guid.Parse("00000000-0000-0000-0000-000000000001");

Belief Make(
    string idStr, Guid entityId, Dimension dim, string criterion,
    double value, SourceTier tier, double confidence, string derivation) =>
    new(
        Id:            Guid.Parse(idStr),
        EntityId:      entityId,
        Dimension:     dim,
        Criterion:     criterion,
        Value:         value,
        SourceTier:    tier,
        Confidence:    confidence,
        Freshness:     1.0,
        Derivation:    derivation,
        SourceSignals: [],
        Version:       1,
        SupersededBy:  null,
        CreatedAt:     anchor,
        TraceId:       trace1);

IReadOnlyList<Belief> iivsBeliefs =
[
    Make("b1000001-0000-0000-0000-000000000001", iivsVendorId,
        Dimension.Operational, "uptime_sla_exists",
        0.85, SourceTier.Primary, 0.90,
        "MSA Section 3.1 specifies 99.9% uptime SLA with measurement period of calendar month"),

    Make("b1000002-0000-0000-0000-000000000002", iivsVendorId,
        Dimension.Operational, "uptime_sla_percentage",
        0.85, SourceTier.Primary, 0.90,
        "Contracted uptime SLA is 99.9% as specified in the executed Master Services Agreement"),

    Make("b1000003-0000-0000-0000-000000000003", iivsVendorId,
        Dimension.Experiential, "sla_met_last_12_months",
        0.80, SourceTier.Verified, 0.82,
        "Monitoring platform reports 99.95% uptime over the past 12 months, exceeding the 99.9% SLA"),

    Make("b1000004-0000-0000-0000-000000000004", iivsVendorId,
        Dimension.Experiential, "csat_score",
        0.75, SourceTier.Inferred, 0.70,
        "CSAT score of 4.2 out of 5.0 recorded in Q3 2024 survey across 12 respondents"),

    Make("b1000005-0000-0000-0000-000000000005", iivsVendorId,
        Dimension.Financial, "signed_contract_with_payment_terms",
        0.90, SourceTier.Primary, 0.95,
        "Master Services Agreement signed 2022-03-15 with net-30 payment terms and auto-renewal clause"),

    Make("b1000006-0000-0000-0000-000000000006", iivsVendorId,
        Dimension.Financial, "annual_contract_value",
        0.80, SourceTier.Primary, 0.95,
        "Annual contract value is $285,000 USD as specified in executed Order Form OF-2022-003"),

    Make("b1000007-0000-0000-0000-000000000007", iivsVendorId,
        Dimension.Strategic, "roadmap_alignment",
        0.70, SourceTier.Reported, 0.60,
        "VP Engineering confirmed vendor roadmap aligns with platform modernisation initiative in Q4 2024 business review"),

    Make("b1000008-0000-0000-0000-000000000008", iivsVendorId,
        Dimension.Strategic, "renewal_date",
        0.80, SourceTier.Primary, 0.90,
        "Contract renewal date is 2025-03-14 as specified in MSA Section 12.2"),
];

IReadOnlyList<Belief> regulusBeliefs =
[
    Make("b2000001-0000-0000-0000-000000000001", regulusVendorId,
        Dimension.Financial, "signed_contract_with_payment_terms",
        0.80, SourceTier.Primary, 0.88,
        "Purchase Order PO-2023-047 signed with net-60 payment terms; total value $42,000"),

    Make("b2000002-0000-0000-0000-000000000002", regulusVendorId,
        Dimension.Financial, "annual_contract_value",
        0.65, SourceTier.Inferred, 0.72,
        "Estimated annual spend of $42,000 inferred from single executed purchase order"),
];

// ── IIVS (rich vendor) ────────────────────────────────────────────────────────

await RunVendor("IIVS (rich)", iivsVendorId, iivsBeliefs);

// ── Regulus (sparse vendor) ───────────────────────────────────────────────────

await RunVendor("Regulus (sparse)", regulusVendorId, regulusBeliefs);

// ── Summary ───────────────────────────────────────────────────────────────────

Console.WriteLine();
Console.WriteLine(new string('═', 72));
if (errors == 0)
{
    Console.WriteLine($"[completeness-recorder] done — cassette → {cassette}");
    Console.WriteLine($"[completeness-recorder] re-run tests to verify:");
    Console.WriteLine($"  dotnet test --filter AnsweringStage");
}
else
{
    Console.Error.WriteLine($"[completeness-recorder] {errors} error(s) — cassette may be incomplete");
    return 1;
}

return 0;

// ── Local functions ───────────────────────────────────────────────────────────

async Task RunVendor(string label, Guid vendorId, IReadOnlyList<Belief> beliefs)
{
    Console.WriteLine(new string('─', 72));
    Console.WriteLine($"  {label}  (vendorId={vendorId})");
    Console.WriteLine($"  Beliefs: {beliefs.Count}");
    Console.WriteLine(new string('─', 72));

    IReadOnlyList<Answer> answers;
    try
    {
        answers = await stage.AnswerAsync(vendorId, questions, beliefs, anchorNow);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"  [ERROR] answering failed: {ex.Message}");
        errors++;
        Console.WriteLine();
        return;
    }

    var profile = CompletenessRubric.Compute(questions, answers);

    Console.WriteLine($"  Overall coverage: {profile.OverallCoverage:P0}  " +
                      $"({profile.AnsweredQuestionIds.Count}/{questions.Count} answered)");
    Console.WriteLine();

    foreach (var a in answers)
    {
        var cited = a.CitedBeliefIds.Count > 0
            ? $"  cited=[{string.Join(", ", a.CitedBeliefIds)}]"
            : "";
        Console.WriteLine($"  {a.QuestionId,-22}  {a.Value,-12}  conf={a.Confidence:F2}{cited}");
    }

    Console.WriteLine();
    Console.WriteLine("  Per-dimension coverage:");
    foreach (var d in profile.DimensionCoverages)
        Console.WriteLine($"    {d.Dimension,-14}  {d.AnsweredCount}/{d.RequiredCount}  ({d.Coverage:P0})");
    Console.WriteLine();
}

static (string? Cassette, DepthLevel? Depth, bool Verify) ParseFlags(string[] args)
{
    string? cassette = null;
    DepthLevel? depth = null;
    bool verify = false;
    for (int i = 0; i < args.Length; i++)
    {
        switch (args[i].ToLowerInvariant())
        {
            case "--cassette": if (i + 1 < args.Length) cassette = args[++i]; break;
            case "--depth":
                if (i + 1 < args.Length && Enum.TryParse<DepthLevel>(args[++i], ignoreCase: true, out var d))
                    depth = d;
                break;
            case "--verify": verify = true; break;
        }
    }
    return (cassette, depth, verify);
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

// Prints the raw LLM JSON before it is stored in the cassette.
sealed class LoggingLlm(IKozmoLlm inner) : IKozmoLlm
{
    public async Task<LlmResult> CompleteJsonAsync(
        string system, string user, int maxTokens = 500, CancellationToken ct = default)
    {
        var result = await inner.CompleteJsonAsync(system, user, maxTokens, ct);
        Console.WriteLine("  [raw] " + (result.Answer is System.Text.Json.JsonElement el
            ? el.GetRawText()
            : "(null)"));
        return result;
    }
}
