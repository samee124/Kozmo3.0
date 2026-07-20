using If.Contracts;
using Kozmo.Contracts;

namespace Po.VendorCall;

/// <summary>
/// Output of VendorCallEvidenceCollector.
/// Raw gathered evidence — no interpretation, scoring, or LLM.
/// The briefing composer (Phase 7b) interprets this bundle.
/// </summary>
public sealed record VendorCallEvidenceBundle(
    /// <summary>Commercial emails from the vendor domain (noise filtered).</summary>
    IReadOnlyList<MailArtifact> RecentEmails,

    /// <summary>Emails excluded as noise (events@, newsletter@, noreply@, etc.).</summary>
    IReadOnlyList<MailArtifact> FilteredNoiseEmails,

    /// <summary>Contract evidence records (SignedContract, ExecutedAgreement, Amendment, etc.).</summary>
    IReadOnlyList<Evidence> Contracts,

    /// <summary>OwnerNote evidence records that have at least one associated belief (meeting notes).</summary>
    IReadOnlyList<Evidence> PriorMeetingNotes,

    /// <summary>OwnerNote evidence records with no associated beliefs (open commitments/action items).</summary>
    IReadOnlyList<Evidence> OpenCommitments,

    /// <summary>Email/Communication evidence records flagged as commercial signals.</summary>
    IReadOnlyList<Evidence> CommercialSignals,

    /// <summary>Human-readable descriptions of things the collector noticed are missing or overdue.</summary>
    IReadOnlyList<string> EvidenceGaps);
