namespace Km.Store.Metadata;

/// <summary>
/// One structured, non-scored fact captured from a document — e.g. a PO reference, a termination
/// clause, a liability cap. Retained for agents to query; never confidence-scored, never read by
/// <c>RubricModule</c>/<c>IndexModule</c>/<c>PostureModule</c>/completeness. That boundary is
/// CI-enforced, not conventional — see the metadata-wall invariant in
/// <c>Kozmo.Architecture.Tests</c>: no scoring assembly may reference the
/// <c>Km.Store.Metadata</c> namespace.
/// <para>
/// E1 Part 7 Step 4: this is the store's shape only. Nothing writes to it yet — extraction into
/// this store is Step 5.
/// </para>
/// </summary>
public sealed record DocumentMetadata(
    Guid           Id,
    Guid           EntityId,
    Guid           DocumentId,
    string         DocumentType,  // e.g. "invoice", "msa", "order_form" — DocTypeInferrer.InferDocType's output
    string         FieldName,     // e.g. "po_reference", "termination_clause", "notice_period"
    string         Value,
    string         Derivation,    // provenance quote, mirrors Belief.Derivation
    DateTimeOffset ObservedAt
);

/// <summary>
/// One entity's full retained metadata set — the agent-facing query surface. Strictly additive to
/// beliefs: things the question banks do NOT ask about (the load-bearing invariant, E1 Part 3).
/// Named on the same EntityId convention as Belief/EntityIndex/PostureAssignment/Signal, not the
/// Evidence/CanonicalVendor outlier — vendor is a label on a generic entity here, not the model.
/// </summary>
public sealed record EntityKnowledge(
    Guid                            EntityId,
    IReadOnlyList<DocumentMetadata> Metadata
);
