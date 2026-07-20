using Wc.Contracts;

namespace Po.VendorCall;

public enum CheckInDispatchStatus
{
    /// <summary>A new check-in was created and transport delivery was attempted.</summary>
    Dispatched,

    /// <summary>An open check-in already existed for this meeting — nothing was created.</summary>
    AlreadyDispatched,

    /// <summary>No pre-meeting question was found in the question bank — nothing was created.</summary>
    NoQuestionsAvailable,
}

/// <summary>Result of VendorCallCheckInPlanner.PlanPreMeetingAsync.</summary>
public sealed record CheckInDispatchResult(
    CheckInDispatchStatus Status,
    CheckIn?              CheckIn = null);
