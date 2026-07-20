namespace Wj.MeetingPulse;

/// <summary>
/// Configuration for the MeetingPulse background worker.
/// Bind from appsettings.json section "MeetingPulse" or via environment variables.
/// </summary>
public sealed class MeetingPulseOptions
{
    public const string Section = "MeetingPulse";

    /// <summary>How often the discovery + processing cycle runs, in seconds (default: 30).</summary>
    public int PollingIntervalSeconds { get; init; } = 30;

    /// <summary>
    /// How far ahead to look for meetings before triggering the pre-meeting brief.
    /// A meeting is processed when StartUtc is within [now, now + LeadTime] (default: 24 hours).
    /// </summary>
    public int PreMeetingLeadTimeHours { get; init; } = 24;

    /// <summary>
    /// Buffer after the meeting end time before advancing status to MeetingEnded (default: 30 minutes).
    /// </summary>
    public int MeetingEndedBufferMinutes { get; init; } = 30;

    /// <summary>
    /// When false (default), post-meeting processing raises a STATUS_SELECT check-in asking
    /// the user how the meeting went. When true, the worker fetches the transcript and
    /// advances the run to TranscriptReady. LLM analysis is NOT performed by the worker —
    /// use the harness post-meeting-transcript command for full LLM extraction.
    /// </summary>
    public bool EnableTranscriptAnalysis { get; init; } = false;

    /// <summary>Email address of the owner — used as recipient for briefings and check-ins.</summary>
    public string OwnerEmail { get; init; } = "";

    /// <summary>
    /// Entra object ID of the signed-in user. Must match the account that signed in
    /// interactively via GraphAuthHarness (used to look up the cached token).
    /// </summary>
    public string UserObjectId { get; init; } = "";

    /// <summary>Absolute or relative path to kozmo-demo.db (default: kozmo-demo.db from working dir walk-up).</summary>
    public string DbPath { get; init; } = "";

    /// <summary>
    /// When true (default), ReviewComposer is constructed with a real LLM client to produce
    /// narrated Q1-Q5 prose. Requires OPENAI_API_KEY to be set. When the key is absent or the
    /// LLM call fails at runtime, ReviewComposer falls back to Mode A deterministic text automatically.
    /// Set to false to disable LLM calls without a code change.
    /// </summary>
    public bool EnableLlmNarrative { get; init; } = true;

    /// <summary>
    /// Maximum time to wait for a Teams transcript to become available after meeting end (default: 4 hours).
    /// If the transcript is still unavailable after this window, the run advances to NoTranscriptAvailable
    /// so it is not retried indefinitely (e.g. cancelled meetings or recordings that were never started).
    /// </summary>
    public int MaxTranscriptWaitHours { get; init; } = 4;
}
