// GENERATED — do not hand-edit; regenerate via tools/codegen/generate.ps1

namespace Kozmo.Contracts;

/// <summary>
/// Represents a source document (PDF, CSV, email, etc.) from which beliefs are extracted.
/// One document → one Evidence row → many Belief rows that cite it via Provenance.
/// </summary>
public sealed record Evidence(
    Guid           EvidenceId,
    Guid           VendorId,
    DocType        DocType,
    SourceTier     SourceTier,  // derived from doc_type via doc_type_tier_map
    string         Ref,         // filename / blob ref
    int            DocVersion,
    DateTimeOffset IngestedAt
);
