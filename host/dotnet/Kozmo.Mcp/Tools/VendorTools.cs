using System.ComponentModel;
using Kozmo.Contracts;
using ModelContextProtocol.Server;
using Rm.Contracts;

namespace Kozmo.Mcp.Tools;

/// <summary>
/// MCP tools that expose the Reality-Model Query Service as read-only vendor intelligence tools.
/// All facts come from the deterministic retrieval layer; the LLM only phrases, never invents.
/// </summary>
[McpServerToolType]
public sealed class VendorTools
{
    private static readonly string ValidDimensions =
        "Operational, Experiential, Financial, Strategic";

    private readonly IVendorQueryService _svc;

    public VendorTools(IVendorQueryService svc) => _svc = svc;

    /// <summary>
    /// Get a complete posture overview for a named vendor: overall stance
    /// (Maintain/Monitor/Renegotiate/Escalate/Remediate), composite score band, rationale,
    /// evidence beliefs, open questions, and any detected contradictions.
    /// Use this as the starting point before drilling into a specific dimension.
    /// </summary>
    [McpServerTool(Name = "vendor_overview")]
    public async Task<string> VendorOverviewAsync(
        [Description("Name of the vendor to look up, e.g. 'Cloudwave Systems Inc.' or just 'Cloudwave'")]
        string vendorName,
        CancellationToken ct)
    {
        var answer = await _svc.AnswerAsync(new VendorQuery(vendorName, null, Aspect.Full), ct);
        return answer.Text;
    }

    /// <summary>
    /// Drill into a single business dimension for a vendor: Operational, Experiential,
    /// Financial, or Strategic. Returns that dimension's score, confidence, evidence beliefs,
    /// and any dimension-specific gaps or contradictions. Use this when the user asks about
    /// financials, operations, customer experience, or strategic factors specifically.
    /// </summary>
    [McpServerTool(Name = "vendor_dimension_detail")]
    public async Task<string> VendorDimensionDetailAsync(
        [Description("Name of the vendor to look up")]
        string vendorName,
        [Description("Which business dimension to drill into: Operational | Experiential | Financial | Strategic")]
        string dimension,
        CancellationToken ct)
    {
        var dim = dimension.Trim().ToUpperInvariant() switch
        {
            "OPERATIONAL"  => (Dimension?)Dimension.Operational,
            "EXPERIENTIAL" => Dimension.Experiential,
            "FINANCIAL"    => Dimension.Financial,
            "STRATEGIC"    => Dimension.Strategic,
            _              => null
        };

        if (dim is null)
            return $"Invalid dimension '{dimension}'. Valid values: {ValidDimensions}.";

        var query  = new VendorQuery(vendorName, null, Aspect.Full, FilterDimension: dim);
        var answer = await _svc.AnswerAsync(query, ct);
        return answer.Text;
    }

    /// <summary>
    /// List what the system is waiting to hear back on for a vendor:
    /// pending owner check-in responses, evidence gaps where no scored belief exists,
    /// and any dimensions that need more data to raise confidence.
    /// Use this to understand what information would most improve the assessment.
    /// </summary>
    [McpServerTool(Name = "vendor_open_questions")]
    public async Task<string> VendorOpenQuestionsAsync(
        [Description("Name of the vendor to look up")]
        string vendorName,
        CancellationToken ct)
    {
        var answer = await _svc.AnswerAsync(new VendorQuery(vendorName, null, Aspect.Gaps), ct);
        return answer.Text;
    }
}