namespace Ig.Contracts;

/// <summary>
/// Identity-specific thresholds for Stage E (IdentityGate).
/// Same role as SaasProfile.Bands for the II pipeline's AssignBand: config-sourced
/// constants fed into the gate pattern, not hardcoded in the gate logic.
/// </summary>
public sealed record IdentityGateConfig(
    /// <summary>
    /// Cluster confidence must be ≥ this AND the cluster must be "strong" (multi-member
    /// OR single PRIMARY source) for Stage E to auto-confirm the vendor.
    /// Conservative start: 0.70 covers Verified-tier (0.80) and Primary-tier (0.95+).
    /// </summary>
    double AutoConfirmMin)
{
    public static readonly IdentityGateConfig Default = new(AutoConfirmMin: 0.70);
}
