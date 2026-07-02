using System.Text.Json;
using Ii.Intake;
using Ii.Spine;
using Km.Store;
using Kozmo.Contracts;
using Kozmo.Contracts.Config;
using Kozmo.Llm;
using Xunit;

namespace Kozmo.VendorFile.Tests;

/// <summary>
/// Class Q — VendorFilePdfLane.ExtractFromBytesAndWriteAsync tests.
///
/// Proves the bytes → PdfTextExtractor → LLM → beliefs pipeline:
///   Q1: record mode writes cassette; returned beliefs carry page: locators.
///   Q2: replay mode hits the cassette written by Q1 (no inner LLM needed).
///
/// Uses a FakeLlm inner so CI stays offline. The sample PDF fixture is the
/// committed 2-page minimal PDF from fixtures/pdf-samples/sample.pdf.
/// </summary>
[Trait("Class", "Q")]
public sealed class PdfByteLaneTests
{
    private static readonly string CataloguePath = FindCatalogueDir();

    // One structural claim with a page+clause locator — enough to prove the provenance round-trip.
    private const string BytesLlmResponse = """
        {
          "claims": [
            {
              "claim_key":   "renewal_date",
              "raw_value":   1756684800,
              "confidence":  0.95,
              "quoted_span": "test renewal clause",
              "page":        1,
              "clause":      "§1"
            }
          ]
        }
        """;

    // ── Q1: record mode ───────────────────────────────────────────────────────
    // Sample PDF bytes → ExtractFromBytesAndWriteAsync with a FakeLlm in record mode.
    // Cassette is populated; returned beliefs must carry "page:" locators.

    [Fact]
    public async Task ExtractFromBytes_RecordMode_WritesCassetteBeliefHasPageLocator()
    {
        var profile     = new Catalogue().Load(CataloguePath);
        var vendorId    = Guid.NewGuid();
        var ev          = MakeEvidence(vendorId);
        var pdfBytes    = LoadSamplePdf();
        var tmpCassette = Path.GetTempFileName();
        try
        {
            var store  = new SqliteEntityStore("Data Source=:memory:", profile);
            var svc    = new VendorFileWriteService(store, profile);
            var recLlm = new CachingLlmClient(tmpCassette, recordMode: true, inner: new FakeLlm(BytesLlmResponse));
            var lane   = new VendorFilePdfLane(recLlm, svc, profile);

            var beliefs = await lane.ExtractFromBytesAndWriteAsync(vendorId, ev, pdfBytes, DemoClock.AsOf);

            Assert.NotEmpty(beliefs);
            Assert.All(beliefs, b =>
            {
                Assert.NotNull(b.Provenance);
                Assert.StartsWith("page:", b.Provenance!.Locator);
            });
        }
        finally { File.Delete(tmpCassette); }
    }

    // ── Q2: replay mode ───────────────────────────────────────────────────────
    // Same bytes in replay mode against the cassette written by the first call.
    // CachingLlmClient has no inner; would throw LlmCacheMissException if the cache missed.

    [Fact]
    public async Task ExtractFromBytes_ReplayMode_HitsCassetteWithoutInnerLlm()
    {
        var profile     = new Catalogue().Load(CataloguePath);
        var vendorId    = Guid.NewGuid();
        var ev          = MakeEvidence(vendorId);
        var pdfBytes    = LoadSamplePdf();
        var tmpCassette = Path.GetTempFileName();
        try
        {
            // Part 1: record — populate cassette with FakeLlm
            {
                var store  = new SqliteEntityStore("Data Source=:memory:", profile);
                var svc    = new VendorFileWriteService(store, profile);
                var recLlm = new CachingLlmClient(tmpCassette, recordMode: true, inner: new FakeLlm(BytesLlmResponse));
                var lane   = new VendorFilePdfLane(recLlm, svc, profile);
                await lane.ExtractFromBytesAndWriteAsync(vendorId, ev, pdfBytes, DemoClock.AsOf);
            }

            // Part 2: replay — no inner LLM; a cache miss would throw LlmCacheMissException
            var store2  = new SqliteEntityStore("Data Source=:memory:", profile);
            var svc2    = new VendorFileWriteService(store2, profile);
            var repLlm  = new CachingLlmClient(tmpCassette, recordMode: false);
            var lane2   = new VendorFilePdfLane(repLlm, svc2, profile);
            var beliefs = await lane2.ExtractFromBytesAndWriteAsync(vendorId, ev, pdfBytes, DemoClock.AsOf);

            Assert.NotEmpty(beliefs);
            Assert.All(beliefs, b =>
            {
                Assert.NotNull(b.Provenance);
                Assert.StartsWith("page:", b.Provenance!.Locator);
            });
        }
        finally { File.Delete(tmpCassette); }
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static Evidence MakeEvidence(Guid vendorId)
        => new Evidence(
            EvidenceId: Guid.NewGuid(),
            VendorId:   vendorId,
            DocType:    DocType.SignedContract,
            SourceTier: SourceTier.Primary,
            Ref:        "contracts/sample.pdf",
            DocVersion: 1,
            IngestedAt: DemoClock.AsOf);

    private static byte[] LoadSamplePdf()
    {
        var dir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(dir))
        {
            var path = Path.Combine(dir, "fixtures", "pdf-samples", "sample.pdf");
            if (File.Exists(path)) return File.ReadAllBytes(path);
            dir = Path.GetDirectoryName(dir);
        }
        throw new FileNotFoundException("fixtures/pdf-samples/sample.pdf not found.");
    }

    private static string FindCatalogueDir()
    {
        var dir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(dir))
        {
            var path = Path.Combine(dir, "catalogue", "profiles", "saas");
            if (Directory.Exists(path)) return path;
            dir = Path.GetDirectoryName(dir);
        }
        throw new DirectoryNotFoundException("catalogue/profiles/saas not found.");
    }

    private sealed class FakeLlm : IKozmoLlm
    {
        private readonly string _json;
        public FakeLlm(string json) => _json = json;
        public Task<LlmResult> CompleteJsonAsync(
            string system, string user, int maxTokens = 500, CancellationToken ct = default)
            => Task.FromResult(new LlmResult(
                Answer:           JsonSerializer.Deserialize<JsonElement>(_json),
                Confidence:       0.95,
                ReasoningSummary: "Fake PDF extraction."));
    }
}
