namespace Rm.Contracts;

/// <summary>
/// Controls which sections of the read model are retrieved and surfaced in the answer.
/// Full = all sections. Narrower aspects reduce retrieval scope for focused questions.
/// </summary>
public enum Aspect
{
    Full,
    Posture,
    Evidence,
    Gaps,
    Contradictions
}
