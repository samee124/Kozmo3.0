using System.Text.Json;
using Kozmo.Llm;
using Po.VendorCall;
using Xunit;

namespace Po.VendorCall.Tests;

/// <summary>
/// Tests for TranscriptComprehensionService.
/// Uses a fixed-response stub LLM to avoid live API calls.
/// The fixture VTT (sample_transcript.vtt) contains a realistic Northstar vendor meeting.
/// The stub JSON mirrors what a real LLM would extract from that transcript — with one
/// fabricated quote (for grounding check) and one low-confidence item (for threshold check).
/// </summary>
public sealed class TranscriptComprehensionServiceTests
{
    // ── Fixture loading ───────────────────────────────────────────────────────

    private static readonly string FixtureVtt =
        File.ReadAllText(ResolveFixturePath("sample_transcript.vtt"));

    private static readonly TranscriptParseResult ParsedFixture =
        new TranscriptParser().Parse(FixtureVtt);

    /// <summary>
    /// Stub LLM response: 9 items from the sample transcript.
    ///   Items 1-7  — grounded (quotes present in transcript), confidence 0.62–0.94
    ///   Item 8     — FABRICATED quote (not in transcript), confidence 0.90 → grounding discard
    ///   Item 9     — grounded but confidence 0.30 → confidence threshold discard
    /// Expected final items: 7 (items 1-7 after filtering).
    /// </summary>
    private const string FixtureJson = """
        {
          "items": [
            {
              "type": "pricing_signal",
              "description": "Northstar proposed 7% annual pricing uplift citing infrastructure investment",
              "speaker": "Daniel Reed",
              "counterParty": "Ritesh P",
              "quote": "The 7% reflects our infrastructure investment and the new analytics module we are rolling out for all customers.",
              "timestamp": "00:00:26",
              "confidence": 0.94,
              "owner": null,
              "dueDate": null
            },
            {
              "type": "decision",
              "description": "Pricing discussion deferred pending receipt of detailed enhancement breakdown",
              "speaker": "Daniel Reed",
              "counterParty": "Ritesh P",
              "quote": "I think the sensible thing is to defer the pricing discussion until I can send you a detailed enhancement breakdown.",
              "timestamp": "00:00:59",
              "confidence": 0.88,
              "owner": null,
              "dueDate": null
            },
            {
              "type": "commitment",
              "description": "Daniel Reed to deliver pricing enhancement breakdown by end of day Friday",
              "speaker": "Daniel Reed",
              "counterParty": "Ritesh P",
              "quote": "I will have the pricing enhancement breakdown to you by end of day Friday.",
              "timestamp": "00:01:19",
              "confidence": 0.92,
              "owner": "Daniel Reed",
              "dueDate": "Friday"
            },
            {
              "type": "commitment",
              "description": "Daniel Reed to deliver Q2 SLA compliance report by Wednesday",
              "speaker": "Daniel Reed",
              "counterParty": "Ritesh P",
              "quote": "I will have it to you by Wednesday of this week without fail.",
              "timestamp": "00:01:41",
              "confidence": 0.93,
              "owner": "Daniel Reed",
              "dueDate": "Wednesday"
            },
            {
              "type": "open_question",
              "description": "Whether SOC 2 Type II certificate was received by legal team",
              "speaker": "Ritesh P",
              "counterParty": "Daniel Reed",
              "quote": "I will check with the legal team.",
              "timestamp": "00:02:23",
              "confidence": 0.76,
              "owner": "Ritesh P",
              "dueDate": null
            },
            {
              "type": "commitment",
              "description": "Daniel Reed to compile utilization data from platform analytics before renewal",
              "speaker": "Daniel Reed",
              "counterParty": "Ritesh P",
              "quote": "I will try to pull together the utilization data from our platform analytics and get it across to you.",
              "timestamp": "00:03:01",
              "confidence": 0.62,
              "owner": "Daniel Reed",
              "dueDate": "before renewal"
            },
            {
              "type": "next_step",
              "description": "Ritesh P to share internal utilization analysis from IT team before renewal discussion",
              "speaker": "Ritesh P",
              "counterParty": "Daniel Reed",
              "quote": "I am going to share our internal utilization analysis from our IT team.",
              "timestamp": "00:03:16",
              "confidence": 0.86,
              "owner": "Ritesh P",
              "dueDate": "before renewal"
            },
            {
              "type": "commitment",
              "description": "Northstar will provide complete financial transparency",
              "speaker": "Daniel Reed",
              "counterParty": null,
              "quote": "We will provide complete financial transparency and a detailed cost breakdown for all pricing components.",
              "timestamp": "00:02:00",
              "confidence": 0.90,
              "owner": "Daniel Reed",
              "dueDate": null
            },
            {
              "type": "pricing_signal",
              "description": "Opening greeting",
              "speaker": "Ritesh P",
              "counterParty": null,
              "quote": "Good morning Daniel, thanks for joining the call today.",
              "timestamp": "00:00:01",
              "confidence": 0.30,
              "owner": null,
              "dueDate": null
            }
          ]
        }
        """;

    private static VendorCallRun MakeRun() => new()
    {
        Id                      = Guid.NewGuid(),
        EventId                 = "northstar-renewal-2026-07-22",
        VendorId                = Guid.Parse("dd000001-0000-0000-0000-000000000001"),
        VendorName              = "Northstar Software",
        MeetingSubject          = "Northstar Software — annual renewal review",
        StartUtc                = new DateTimeOffset(2026, 7, 22, 10, 0, 0, TimeSpan.Zero),
        EndUtc                  = new DateTimeOffset(2026, 7, 22, 11, 0, 0, TimeSpan.Zero),
        SignedInUserPrincipalId = "rishi@econtracts.onmicrosoft.com",
        Status                  = VendorCallStatus.TranscriptReady,
        CreatedAt               = DateTimeOffset.UtcNow,
        UpdatedAt               = DateTimeOffset.UtcNow,
    };

    private static TranscriptComprehensionContext MakeContext(
        string[]? openItems = null) => new(
        Run:                  MakeRun(),
        ParsedTranscript:     ParsedFixture,
        PreMeetingBriefing:   null,
        OpenItemsFromBrief:   openItems ?? []);

    private static ITranscriptComprehensionService MakeService(IKozmoLlm? llm) =>
        new TranscriptComprehensionService(llm);

    private static async Task<TranscriptExtractionResult> RunFixtureAsync(
        IKozmoLlm? llm = null, string[]? openItems = null)
    {
        var svc = MakeService(llm ?? new FixedLlm(FixtureJson));
        return await svc.ExtractAsync(MakeContext(openItems), CancellationToken.None);
    }

    // ── Extraction correctness ────────────────────────────────────────────────

    [Fact]
    public async Task Extract_ClearCommitment_IsExtracted()
    {
        var result = await RunFixtureAsync();
        var commit = result.Items.FirstOrDefault(i =>
            i.Description.Contains("pricing enhancement breakdown") &&
            i.Type == TranscriptItemType.Commitment);
        Assert.NotNull(commit);
        Assert.Equal("Daniel Reed", commit.Owner);
        Assert.Equal("Friday", commit.DueDate);
    }

    [Fact]
    public async Task Extract_HighConfidence_NoConfirmationRequired()
    {
        var result = await RunFixtureAsync();
        // Items 1-4 and 7 are ≥0.85 — none should require confirmation
        var highConf = result.Items.Where(i => i.Confidence >= 0.85).ToList();
        Assert.All(highConf, i => Assert.False(i.RequiresUserConfirmation));
    }

    [Fact]
    public async Task Extract_MediumConfidence_RequiresConfirmation()
    {
        var result = await RunFixtureAsync();
        // Item 6 (utilization, 0.62) and item 5 (SOC2 open_question, 0.76) are 0.55–0.84
        var medConf = result.Items.Where(i => i.Confidence >= 0.55 && i.Confidence < 0.85).ToList();
        Assert.NotEmpty(medConf);
        Assert.All(medConf, i => Assert.True(i.RequiresUserConfirmation));
    }

    [Fact]
    public async Task Extract_LowConfidence_IsDiscarded()
    {
        var result = await RunFixtureAsync();
        // Item 9 (opening greeting, 0.30) must not appear
        var lowConfItem = result.Items.FirstOrDefault(i => i.Description.Contains("Opening greeting"));
        Assert.Null(lowConfItem);
    }

    // ── Grounding check ───────────────────────────────────────────────────────

    [Fact]
    public async Task Extract_GroundingCheck_FabricatedQuote_IsDiscarded()
    {
        var result = await RunFixtureAsync();
        // Item 8: "We will provide complete financial transparency..." is not in transcript
        var fabricated = result.Items.FirstOrDefault(i =>
            i.Description.Contains("complete financial transparency"));
        Assert.Null(fabricated);
    }

    [Fact]
    public async Task Extract_GroundedItems_AreRetained()
    {
        var result = await RunFixtureAsync();
        // After discarding item 8 (grounding) and item 9 (low conf), 7 items remain
        Assert.Equal(7, result.Items.Count);
    }

    // ── Claim key mapping ─────────────────────────────────────────────────────

    [Fact]
    public async Task Extract_CommitmentClaimKey_IsVendorCommitment()
    {
        var result = await RunFixtureAsync();
        var commit = result.Items.First(i => i.Type == TranscriptItemType.Commitment);
        Assert.Equal("vendor.commitment.description", commit.ClaimKey);
    }

    [Fact]
    public async Task Extract_PricingSignalClaimKey_IsVendorCommunication()
    {
        var result = await RunFixtureAsync();
        var signal = result.Items.First(i => i.Type == TranscriptItemType.PricingSignal);
        Assert.Equal("vendor.communication.pricing_signal", signal.ClaimKey);
    }

    [Fact]
    public async Task Extract_OpenQuestionClaimKey_IsVendorEvidenceGap()
    {
        var result = await RunFixtureAsync();
        var question = result.Items.First(i => i.Type == TranscriptItemType.OpenQuestion);
        Assert.Equal("vendor.evidence_gap", question.ClaimKey);
    }

    [Fact]
    public async Task Extract_NextStepClaimKey_IsVendorCommitment()
    {
        var result = await RunFixtureAsync();
        var nextStep = result.Items.First(i => i.Type == TranscriptItemType.NextStep);
        Assert.Equal("vendor.commitment.description", nextStep.ClaimKey);
    }

    // ── LLM failure handling ──────────────────────────────────────────────────

    [Fact]
    public async Task Extract_LlmNull_ReturnsEmptyResult()
    {
        var svc    = MakeService(null);
        var result = await svc.ExtractAsync(MakeContext(), CancellationToken.None);
        Assert.Empty(result.Items);
        Assert.Equal(0, result.Metadata.TotalItemsExtracted);
    }

    [Fact]
    public async Task Extract_LlmThrows_ReturnsEmptyResult()
    {
        var result = await RunFixtureAsync(llm: new ThrowingLlm());
        Assert.Empty(result.Items);
        Assert.Equal(0, result.Metadata.TotalItemsExtracted);
    }

    [Fact]
    public async Task Extract_LlmInvalidJson_ReturnsEmptyResult()
    {
        // LLM returns JSON object without "items" property — parsing fails on all attempts
        var result = await RunFixtureAsync(llm: new InvalidFormatLlm());
        Assert.Empty(result.Items);
        Assert.Equal(0, result.Metadata.TotalItemsExtracted);
    }

    // ── Pre-brief cross-reference ─────────────────────────────────────────────

    [Fact]
    public async Task Extract_PreBriefItem_PricingUplift_IsAddressed()
    {
        var result = await RunFixtureAsync(openItems: ["7% pricing uplift"]);

        Assert.Single(result.ResolvedPreBriefItems);
        var resolution = result.ResolvedPreBriefItems[0];
        Assert.Equal("7% pricing uplift", resolution.PreBriefItem);
        Assert.True(resolution.AddressedInMeeting);
        Assert.NotNull(resolution.TranscriptEvidence);
    }

    [Fact]
    public async Task Extract_PreBriefItem_AutoRenewal_IsNotAddressed()
    {
        var result = await RunFixtureAsync(openItems: ["auto-renewal clause review"]);

        Assert.Single(result.ResolvedPreBriefItems);
        var resolution = result.ResolvedPreBriefItems[0];
        Assert.False(resolution.AddressedInMeeting);
        Assert.Null(resolution.TranscriptEvidence);
    }

    // ── Metadata ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Extract_Metadata_CountsAreCorrect()
    {
        var result = await RunFixtureAsync();

        // 20 segments in the fixture VTT
        Assert.Equal(20, result.Metadata.TotalSegmentsProcessed);
        // 7 items after grounding (−1) and confidence (−1) filtering
        Assert.Equal(7, result.Metadata.TotalItemsExtracted);
        // ≥0.85: items 1(0.94), 2(0.88), 3(0.92), 4(0.93), 7(0.86) = 5
        Assert.Equal(5, result.Metadata.HighConfidenceCount);
        // 0.55–0.84: items 5(0.76), 6(0.62) = 2
        Assert.Equal(2, result.Metadata.RequiresConfirmationCount);
        // item 8 grounding fail + item 9 low confidence = 2
        Assert.Equal(2, result.Metadata.DiscardedLowConfidenceCount);
    }

    // ── Edge cases ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Extract_EmptyTranscript_ReturnsEmptyResult()
    {
        var svc     = new TranscriptComprehensionService(new FixedLlm(FixtureJson));
        var context = new TranscriptComprehensionContext(
            MakeRun(),
            new TranscriptParser().Parse(""),
            null,
            []);

        var result = await svc.ExtractAsync(context, CancellationToken.None);

        Assert.Empty(result.Items);
        Assert.Equal(0, result.Metadata.TotalSegmentsProcessed);
    }

    [Fact]
    public void ChunkSegments_LargeTranscript_ProducesMultipleChunks()
    {
        // 110 segments × 30 words = 3300 words > 3000 threshold
        var segments = Enumerable.Range(0, 110)
            .Select(i => new TranscriptSegment(
                "Speaker A",
                TimeSpan.FromMinutes(i),
                TimeSpan.FromMinutes(i + 1),
                string.Join(" ", Enumerable.Repeat("word", 30))))
            .ToList();

        var chunks = TranscriptComprehensionService.ChunkSegments(segments);

        Assert.True(chunks.Count > 1, $"Expected >1 chunk for 3300-word transcript but got {chunks.Count}");
        // All original segments are covered across chunks
        Assert.True(chunks.Sum(c => c.Count) >= 110);
    }

    // ── Fixture path helper ───────────────────────────────────────────────────

    private static string ResolveFixturePath(string fileName)
    {
        var dir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(dir))
        {
            var candidate = Path.Combine(dir, "fixtures", fileName);
            if (File.Exists(candidate)) return candidate;
            var parent = Path.GetDirectoryName(dir);
            if (parent == dir) break;
            dir = parent;
        }
        throw new FileNotFoundException($"Test fixture not found: {fileName}");
    }

    // ── Stub LLM implementations ──────────────────────────────────────────────

    private sealed class FixedLlm : IKozmoLlm
    {
        private readonly string _json;
        public FixedLlm(string json) => _json = json;

        public Task<LlmResult> CompleteJsonAsync(
            string system, string user, int maxTokens = 500, CancellationToken ct = default)
        {
            var el = JsonSerializer.Deserialize<JsonElement>(_json);
            return Task.FromResult(new LlmResult(el, 0.9, "test extraction"));
        }
    }

    private sealed class ThrowingLlm : IKozmoLlm
    {
        public Task<LlmResult> CompleteJsonAsync(
            string system, string user, int maxTokens = 500, CancellationToken ct = default)
            => throw new InvalidOperationException("LLM unavailable");
    }

    private sealed class InvalidFormatLlm : IKozmoLlm
    {
        public Task<LlmResult> CompleteJsonAsync(
            string system, string user, int maxTokens = 500, CancellationToken ct = default)
        {
            // Returns an object with no "items" — parsing will always fail
            var el = JsonSerializer.Deserialize<JsonElement>("{\"error\":\"unable to extract\"}");
            return Task.FromResult(new LlmResult(el, 0.0, "error"));
        }
    }
}
