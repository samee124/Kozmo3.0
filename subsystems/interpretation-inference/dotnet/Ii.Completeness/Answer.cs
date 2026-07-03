namespace Ii.Completeness;

/// <summary>
/// An answer to a completeness question, produced by the answering stage (Commit 2) or
/// supplied by hand for rubric testing (Commit 1). The positional constructor preserves
/// Commit-1 call sites; Commit-2 fields are init-only with safe defaults.
/// </summary>
public sealed record Answer(
    string QuestionId,
    string Value,
    double Confidence)
{
    /// <summary>Vendor this answer belongs to. Guid.Empty for hand-supplied test answers.</summary>
    public Guid VendorId { get; init; } = Guid.Empty;

    /// <summary>Belief IDs the answering-stage LLM cited as evidence. Empty for hand-supplied answers.</summary>
    public IReadOnlyList<Guid> CitedBeliefIds { get; init; } = [];

    /// <summary>True when a belief that was cited has since changed — triggers re-answering (Commit 3).</summary>
    public bool IsDirty { get; init; } = false;

    /// <summary>When the answer was produced. Default for hand-supplied answers.</summary>
    public DateTimeOffset AnsweredAt { get; init; } = default;
}
