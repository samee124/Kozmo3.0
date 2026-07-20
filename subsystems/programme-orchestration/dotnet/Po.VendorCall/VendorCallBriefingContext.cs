using Kozmo.Contracts;
using If.Contracts;

namespace Po.VendorCall;

/// <summary>
/// Everything VendorCallBriefingComposer needs to produce a pre-meeting brief.
/// Assembled from the Phase 7a evidence bundle + beliefs from the entity store.
/// </summary>
public sealed record VendorCallBriefingContext(
    CalendarArtifact              Meeting,
    string                        VendorName,
    Guid                          VendorId,

    /// <summary>Current wall-clock time. Composer never reads the clock directly.</summary>
    DateTimeOffset                Now,

    VendorCallRecipe              Recipe,

    IReadOnlyList<MailArtifact>   RecentEmails,
    IReadOnlyList<Evidence>       Contracts,
    IReadOnlyList<Evidence>       PriorMeetingNotes,
    IReadOnlyList<Evidence>       OpenCommitments,
    IReadOnlyList<Evidence>       CommercialSignals,
    IReadOnlyList<string>         EvidenceGaps,

    /// <summary>
    /// Current (non-superseded) beliefs for this vendor.
    /// Used to extract structural claim values: annual_value, renewal_date,
    /// notice_period, sla_uptime, renewal_intent, etc.
    /// </summary>
    IReadOnlyList<Belief>         CurrentBeliefs,

    CheckInDispatchResult?        PreMeetingCheckInResult);
