namespace Rm.Contracts;

/// <summary>
/// Reality-Model Query Service — channel-agnostic entry point.
/// Implements parse → retrieve → compose over the existing read model.
/// The LLM is used only for phrasing; all facts come from deterministic retrieval.
/// </summary>
public interface IVendorQueryService
{
    Task<VendorQueryAnswer> AnswerAsync(VendorQuery query, CancellationToken ct = default);
}
