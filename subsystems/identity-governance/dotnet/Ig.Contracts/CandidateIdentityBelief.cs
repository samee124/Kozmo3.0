using Kozmo.Contracts;

namespace Ig.Contracts;

public sealed record Provenance(
    string  DocId,
    string? Page,
    string? Span
);

public sealed record CandidateSignals(
    string? Domain,
    string? Address,
    string? TaxId,
    string? Country
);

public sealed record CandidateIdentityBelief(
    Guid              CandidateId,
    string            RawName,
    SourceTier        SourceTier,
    double            Confidence,
    Provenance        Provenance,
    CandidateSignals? Signals,
    string?           RoleHint
);
