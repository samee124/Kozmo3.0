// GENERATED — do not hand-edit; regenerate via tools/codegen/generate.ps1

using System.Collections.Generic;

namespace Kozmo.Contracts;

public sealed record EntityIndex(
    Guid           EntityId,
    IReadOnlyDictionary<Dimension, DimensionScore> DimensionScores,
    double         Composite,
    double         ConfidenceFloor,    // min confidence across dims with beliefs; CRITICAL gate = 0.60
    Band           Band,
    string         Fingerprint,        // hex-SHA256 of sorted beliefs ⊕ scores ⊕ weights ⊕ config_version
    int            Version,
    DateTimeOffset ComputedAt
);
