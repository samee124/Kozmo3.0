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
    double     RequiredConfidence);
