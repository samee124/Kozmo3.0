namespace Ig.Contracts;

/// <summary>
/// LLM-backed fallback for entity-type classification when deterministic rules are
/// insufficient. Only called for genuinely ambiguous candidates. Stub with a fake in
/// offline tests (same pattern as the PDF-lane tests).
/// </summary>
public interface IEntityTypeClassifier
{
    Task<EntityType> ClassifyAsync(
        string            effectiveName,
        string            comparisonKey,
        CancellationToken ct = default);
}
