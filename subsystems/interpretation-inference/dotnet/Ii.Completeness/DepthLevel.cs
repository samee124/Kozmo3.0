namespace Ii.Completeness;

/// <summary>
/// Effort ladder for completeness assessment. L1 = baseline (all vendors);
/// L2/L3 reserved for vendors that warrant deeper scrutiny.
/// Selection includes all questions at depth ≤ the vendor's assigned level.
/// </summary>
public enum DepthLevel { L1 = 1, L2 = 2, L3 = 3 }
