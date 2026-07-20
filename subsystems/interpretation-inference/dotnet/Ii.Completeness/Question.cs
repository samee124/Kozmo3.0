using Kozmo.Contracts;

namespace Ii.Completeness;

/// <summary>
/// An authored, versioned completeness question. Questions are doctrine — they define
/// what "complete" means for a vendor category. The set is fixed between runs and
/// evolves only offline (Wisdom Loop, §7 of spec).
/// </summary>
public sealed record Question(
    string     Id,
    Dimension  Dimension,
    string     Text,
    AnswerType AnswerType,
    DepthLevel DepthLevel,
    double     RequiredConfidence,
    // E2 bridge — optional. When set, this is a claim_key_catalogue key (e.g. "sla_uptime") whose
    // dimension and rubric_criterion are looked up from the catalogue, not duplicated here. A human
    // answer to a bound question is banded through that rubric criterion into a real scored belief
    // (see GapCheckInStage/ProcessCheckInService). Null (the default) keeps the exact prior
    // present-field-only behavior — no regression for any unbound question.
    string?    TargetClaimKey = null);
