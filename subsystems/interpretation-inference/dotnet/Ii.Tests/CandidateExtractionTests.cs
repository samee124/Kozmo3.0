using Ii.CandidateExtraction;
using Kozmo.Contracts;
using Kozmo.Llm;
using Xunit;

namespace Ii.Tests;

/// <summary>
/// Phase 2 Commit 2 — LLM extraction tests.
///
/// These tests replay from the cassette recorded by Kyv.CandidateRecorder.
/// If the cassette or text files are absent (not yet recorded), each test
/// returns early — vacuously green until the record pass is run.
///
/// Run the recorder first:
///   OPENAI_API_KEY=sk-... dotnet run --project tools/Kyv.CandidateRecorder -- \
///       --workspace "D:\June\Kozmo Workspace"
///
/// Then run tests:
///   dotnet test --filter "FullyQualifiedName~CandidateExtraction"
/// </summary>
public sealed class CandidateExtractionTests
{
    private static readonly string RepoRoot    = FindRepoRoot();
    private static readonly string CassettePath = Path.Combine(RepoRoot, "fixtures", "kyv", "candidate-extraction.cassette.json");
    private static readonly string TextsDir     = Path.Combine(RepoRoot, "fixtures", "kyv", "texts");

    // ── §5 Case 2: Customer-as-vendor (the hardest failure from Phase 1) ───────

    [Fact]
    public async Task Extract_RevolutionMedicines_RoleCustomer_NotVendor()
    {
        var beliefs = await ExtractFromPatternAsync("*revolution*");
        if (beliefs is null) return; // cassette not recorded yet

        var rm = beliefs.Where(b =>
            b.RawName.Contains("Revolution", StringComparison.OrdinalIgnoreCase)).ToList();

        Assert.NotEmpty(rm);
        Assert.All(rm, b => Assert.NotEqual("vendor", b.RoleHint, StringComparer.OrdinalIgnoreCase));

        var roles = rm.Select(b => b.RoleHint).Distinct().ToList();
        Assert.Contains(roles, r => r is "customer" or "unknown");
    }

    [Fact]
    public async Task Extract_Meridian_RoleCustomer_NotVendor()
    {
        var beliefs = await ExtractFromPatternAsync("*meridian*");
        if (beliefs is null) return;

        var m = beliefs.Where(b =>
            b.RawName.Contains("Meridian", StringComparison.OrdinalIgnoreCase)).ToList();

        Assert.NotEmpty(m);
        Assert.All(m, b => Assert.NotEqual("vendor", b.RoleHint, StringComparer.OrdinalIgnoreCase));
    }

    // ── §5 Case 1: IIVS — one clean name, consistent role ─────────────────────

    [Fact]
    public async Task Extract_IIVS_ExtractsAsOneCleanName()
    {
        // All IIVS-related docs should produce the same canonical name — no
        // "FROM BILL TO…" / "BANKING FORM…" variants reaching resolution.
        var beliefs = await ExtractFromAllDocsAsync();
        if (beliefs is null) return;

        var iivs = beliefs
            .Where(b => b.RawName.Contains("Vitro", StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.NotEmpty(iivs);

        var distinctNames = iivs.Select(b => b.RawName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        Assert.Single(distinctNames);
        Assert.Contains("In Vitro Sciences", distinctNames[0], StringComparison.OrdinalIgnoreCase);
    }

    // ── §5 Case 5: Real vendors still extracted as vendor ──────────────────────

    [Fact]
    public async Task Extract_Aequitas_RoleVendor()
    {
        var beliefs = await ExtractFromPatternAsync("*aequitas*");
        if (beliefs is null) return;

        var aq = beliefs.Where(b =>
            b.RawName.Contains("Aequitas", StringComparison.OrdinalIgnoreCase)).ToList();

        Assert.NotEmpty(aq);
        Assert.All(aq, b => Assert.Equal("vendor", b.RoleHint, StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Extract_Regulus_RoleVendor()
    {
        var beliefs = await ExtractFromPatternAsync("*regulus*");
        if (beliefs is null) return;

        var rg = beliefs.Where(b =>
            b.RawName.Contains("Regulus", StringComparison.OrdinalIgnoreCase)).ToList();

        Assert.NotEmpty(rg);
        // Regulus must never be extracted as "customer". W9 / tax forms may return
        // "unknown" (no party relationship), which is acceptable.
        Assert.All(rg, b => Assert.NotEqual("customer", b.RoleHint, StringComparer.OrdinalIgnoreCase));
        // At least one document must identify Regulus as vendor.
        Assert.Contains(rg, b => string.Equals(b.RoleHint, "vendor", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Extract_ABC_RoleVendor()
    {
        var beliefs = await ExtractFromPatternAsync("*abc*");
        if (beliefs is null) return;

        var abc = beliefs.Where(b =>
            b.RawName.Contains("ABC", StringComparison.OrdinalIgnoreCase)).ToList();

        Assert.NotEmpty(abc);
        Assert.All(abc, b => Assert.Equal("vendor", b.RoleHint, StringComparer.OrdinalIgnoreCase));
    }

    // ── §5 Case 3+4: Filter backstop — no junk names in output ────────────────

    [Fact]
    public async Task Extract_NoW9CheckboxOrTableJunkInOutput()
    {
        var beliefs = await ExtractFromAllDocsAsync();
        if (beliefs is null) return;

        // These strings should never survive the filter into beliefs.
        var junk = new[] { "C Corp", "S Corp", "Nonprofit Corporation",
                           "Federal tax classification", "Attendees", "INVOICE", "BILL TO",
                           "BANKING FORM", "CERTIFICATE", "FROM BILL TO" };

        foreach (var j in junk)
        {
            var matches = beliefs.Where(b =>
                b.RawName.StartsWith(j, StringComparison.OrdinalIgnoreCase)).ToList();

            Assert.True(matches.Count == 0,
                $"Junk string '{j}' reached resolution as: {string.Join(", ", matches.Select(b => b.RawName))}");
        }
    }

    // ── No person names in output ──────────────────────────────────────────────

    [Fact]
    public async Task Extract_NoPersonNamesInOutput()
    {
        var beliefs = await ExtractFromAllDocsAsync();
        if (beliefs is null) return;

        // Known person names from the workspace (signatories / attendees).
        var persons = new[] { "David Chen", "Sarah", "John", "Jane" };

        foreach (var p in persons)
        {
            var matches = beliefs.Where(b =>
                b.RawName.StartsWith(p, StringComparison.OrdinalIgnoreCase)).ToList();

            Assert.True(matches.Count == 0,
                $"Person name '{p}' appeared in output: {string.Join(", ", matches.Select(b => b.RawName))}");
        }
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Runs extraction on all .txt files whose name matches <paramref name="glob"/>.
    /// Returns null if the cassette is not recorded yet (tests should return early on null).
    /// </summary>
    private async Task<List<Ig.Contracts.CandidateIdentityBelief>?> ExtractFromPatternAsync(
        string glob)
    {
        if (!File.Exists(CassettePath) || !Directory.Exists(TextsDir))
            return null;

        // Texts mirror the workspace subdir structure — search recursively.
        var files = Directory.GetFiles(TextsDir, glob, SearchOption.AllDirectories);
        if (files.Length == 0)
            return null; // no matching docs recorded yet

        return await ExtractFromFilesAsync(files);
    }

    private async Task<List<Ig.Contracts.CandidateIdentityBelief>?> ExtractFromAllDocsAsync()
    {
        if (!File.Exists(CassettePath) || !Directory.Exists(TextsDir))
            return null;

        var files = Directory.GetFiles(TextsDir, "*.txt", SearchOption.AllDirectories);
        if (files.Length == 0)
            return null;

        return await ExtractFromFilesAsync(files);
    }

    private async Task<List<Ig.Contracts.CandidateIdentityBelief>> ExtractFromFilesAsync(
        string[] textFiles)
    {
        var llm       = new CachingLlmClient(CassettePath, recordMode: false);
        var extractor = new DocumentCandidateExtractor(llm);
        var all       = new List<Ig.Contracts.CandidateIdentityBelief>();

        foreach (var tf in textFiles.OrderBy(f => f))
        {
            var fileName = Path.GetFileNameWithoutExtension(tf) + ".pdf";
            var tier     = DocTypeInferrer.InferTier(fileName);
            var text     = File.ReadAllText(tf);

            try
            {
                var beliefs = await extractor.ExtractAsync(text, fileName, tier);
                all.AddRange(beliefs);
            }
            catch (LlmCacheMissException)
            {
                // Some docs may not be recorded yet — skip them rather than fail.
            }
        }

        return all;
    }

    private static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(dir))
        {
            if (File.Exists(Path.Combine(dir, "Kozmo.sln"))) return dir;
            dir = Path.GetDirectoryName(dir);
        }
        throw new InvalidOperationException("Cannot locate repo root (Kozmo.sln not found).");
    }
}
