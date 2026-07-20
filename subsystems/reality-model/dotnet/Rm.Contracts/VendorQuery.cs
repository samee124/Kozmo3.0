using Kozmo.Contracts;

namespace Rm.Contracts;

/// <summary>
/// Channel-agnostic input to the Reality-Model Query Service.
/// Front doors (Slack, Copilot) may pre-fill ResolvedVendorId / Aspect when they can;
/// the service handles the raw-text-only case too.
///
/// FilterDimension (optional): when set, the retriever scopes beliefs, gaps, and contradictions
/// to that single dimension, and the composer phrases a dimension-focused answer.
/// Orthogonal to Aspect â€” combine freely.
/// </summary>
public sealed record VendorQuery(
    string     RawText,
    Guid?      ResolvedVendorId = null,
    Aspect     Aspect           = Aspect.Full,
    Dimension? FilterDimension  = null
);