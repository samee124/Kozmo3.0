// GENERATED — do not hand-edit; regenerate via tools/codegen/generate.ps1

namespace Kozmo.Contracts;

/// <summary>Points a belief back to the evidence document it was extracted from.</summary>
public sealed record BeliefProvenance(
    Guid   EvidenceId,
    string Locator    // e.g. "page:3 §4.1", "row:12", "cell:B,14", "field:annual_value", "message_ref:xyz"
);
