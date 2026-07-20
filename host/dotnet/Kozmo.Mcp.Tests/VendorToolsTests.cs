using Kozmo.Contracts;
using Kozmo.Mcp.Tools;
using Rm.Contracts;
using Xunit;

namespace Kozmo.Mcp.Tests;

/// <summary>
/// Unit tests for VendorTools — verifies that each MCP tool:
///   - Passes the correct Aspect to IVendorQueryService.AnswerAsync
///   - Returns the Text from VendorQueryAnswer unchanged
///   - Maps dimension strings correctly (vendor_dimension_detail)
///   - Rejects invalid dimension strings without calling the service
/// </summary>
public sealed class VendorToolsTests
{
    // ── T1: vendor_overview uses Aspect.Full ─────────────────────────────

    [Fact]
    public async Task VendorOverview_PassesFullAspect()
    {
        var stub  = new StubVendorQueryService { ReplyText = "Full overview text" };
        var tools = new VendorTools(stub);

        var result = await tools.VendorOverviewAsync("Cloudwave", CancellationToken.None);

        Assert.Equal(Aspect.Full, stub.LastQuery!.Aspect);
        Assert.Equal("Cloudwave",  stub.LastQuery.RawText);
        Assert.Equal("Full overview text", result);
    }

    // ── T2: vendor_dimension_detail maps known dimension strings ─────────

    [Theory]
    [InlineData("Operational",  Dimension.Operational)]
    [InlineData("operational",  Dimension.Operational)]
    [InlineData("OPERATIONAL",  Dimension.Operational)]
    [InlineData("Experiential", Dimension.Experiential)]
    [InlineData("Financial",    Dimension.Financial)]
    [InlineData("Strategic",    Dimension.Strategic)]
    public async Task VendorDimensionDetail_MapsKnownDimensions(string dimensionInput, Dimension expected)
    {
        var stub  = new StubVendorQueryService { ReplyText = "detail" };
        var tools = new VendorTools(stub);

        await tools.VendorDimensionDetailAsync("Cloudwave", dimensionInput, CancellationToken.None);

        Assert.Equal(expected,      stub.LastQuery!.FilterDimension);
        Assert.Equal(Aspect.Full,   stub.LastQuery.Aspect);
    }

    // ── T3: vendor_dimension_detail rejects unknown dimension ─────────────
    // Invalid dimension → returns an error string; service is NOT called.

    [Theory]
    [InlineData("")]
    [InlineData("Posture")]
    [InlineData("All")]
    [InlineData("unknown")]
    public async Task VendorDimensionDetail_InvalidDimension_ReturnsErrorNotCallsService(string dimensionInput)
    {
        var stub  = new StubVendorQueryService();
        var tools = new VendorTools(stub);

        var result = await tools.VendorDimensionDetailAsync("Cloudwave", dimensionInput, CancellationToken.None);

        // Service must NOT have been called
        Assert.Null(stub.LastQuery);
        // Result must describe the error and list valid options
        Assert.Contains("Invalid dimension", result, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Operational",       result, StringComparison.OrdinalIgnoreCase);
    }

    // ── T4: vendor_open_questions uses Aspect.Gaps ───────────────────────

    [Fact]
    public async Task VendorOpenQuestions_PassesGapsAspect()
    {
        var stub  = new StubVendorQueryService { ReplyText = "gaps text" };
        var tools = new VendorTools(stub);

        var result = await tools.VendorOpenQuestionsAsync("Cloudwave", CancellationToken.None);

        Assert.Equal(Aspect.Gaps, stub.LastQuery!.Aspect);
        Assert.Equal("gaps text", result);
    }

    // ── T5: all tools forward vendor name as RawText ──────────────────────

    [Fact]
    public async Task AllTools_ForwardVendorNameAsRawText()
    {
        var stub  = new StubVendorQueryService();
        var tools = new VendorTools(stub);

        await tools.VendorOverviewAsync("Meridian IT Services Ltd.", CancellationToken.None);
        Assert.Equal("Meridian IT Services Ltd.", stub.LastQuery!.RawText);

        await tools.VendorDimensionDetailAsync("Corvus", "Financial", CancellationToken.None);
        Assert.Equal("Corvus", stub.LastQuery!.RawText);

        await tools.VendorOpenQuestionsAsync("Helix", CancellationToken.None);
        Assert.Equal("Helix", stub.LastQuery!.RawText);
    }

    // ── T6: tool text is returned exactly from VendorQueryAnswer ─────────

    [Fact]
    public async Task Tools_ReturnAnswerTextVerbatim()
    {
        const string expected = "Vendor is in Renegotiate posture with 72% confidence.";
        var stub  = new StubVendorQueryService { ReplyText = expected };
        var tools = new VendorTools(stub);

        var r1 = await tools.VendorOverviewAsync("Cloudwave", CancellationToken.None);
        var r2 = await tools.VendorDimensionDetailAsync("Cloudwave", "Financial", CancellationToken.None);
        var r3 = await tools.VendorOpenQuestionsAsync("Cloudwave", CancellationToken.None);

        Assert.Equal(expected, r1);
        Assert.Equal(expected, r2);
        Assert.Equal(expected, r3);
    }

    // ── T7: ResolvedVendorId is always null (tools use raw name lookup) ───

    [Fact]
    public async Task AllTools_PassNullResolvedVendorId()
    {
        var stub  = new StubVendorQueryService();
        var tools = new VendorTools(stub);

        await tools.VendorOverviewAsync("Cloudwave", CancellationToken.None);
        Assert.Null(stub.LastQuery!.ResolvedVendorId);

        await tools.VendorDimensionDetailAsync("Cloudwave", "Operational", CancellationToken.None);
        Assert.Null(stub.LastQuery!.ResolvedVendorId);

        await tools.VendorOpenQuestionsAsync("Cloudwave", CancellationToken.None);
        Assert.Null(stub.LastQuery!.ResolvedVendorId);
    }

    // ── Stub ─────────────────────────────────────────────────────────────

    private sealed class StubVendorQueryService : IVendorQueryService
    {
        public VendorQuery?  LastQuery  { get; private set; }
        public string        ReplyText  { get; set; } = "stub reply";

        public Task<VendorQueryAnswer> AnswerAsync(VendorQuery query, CancellationToken ct = default)
        {
            LastQuery = query;
            return Task.FromResult(new VendorQueryAnswer(ReplyText, null));
        }
    }
}
