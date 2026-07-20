using Kozmo.Llm;

namespace Wj.MeetingPulse;

/// <summary>
/// Holds the optional live LLM client for dependency injection into MeetingPulseWorker.
/// Llm is null when OPENAI_API_KEY is absent or EnableLlmNarrative is false — in that case
/// ReviewComposer automatically falls back to its Mode A deterministic path.
/// </summary>
public sealed class WorkerLlmProvider
{
    public IKozmoLlm? Llm { get; }

    public WorkerLlmProvider(IKozmoLlm? llm) => Llm = llm;
}
