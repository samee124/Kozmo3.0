using If.Contracts;

namespace Po.VendorCall;

/// <summary>
/// Input bundle for VendorCallEvidenceCollector and VendorCallCheckInPlanner.
/// Contains the recognized meeting, the matched vendor, and the loaded recipe config.
/// </summary>
public sealed record VendorCallContext(
    CalendarArtifact      Meeting,
    VendorEntityMatchResult Match,
    IReadOnlyList<string> VendorDomains,
    string                SignedInUserPrincipalId,
    VendorCallRecipe      Recipe);
