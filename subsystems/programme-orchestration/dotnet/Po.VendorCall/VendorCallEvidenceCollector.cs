using Kozmo.Contracts;
using Kozmo.Contracts.Interfaces;
using If.Contracts;

namespace Po.VendorCall;

/// <summary>
/// Gathers and classifies all vendor-call evidence for a matched meeting.
/// Does NOT interpret, score, or call LLM — pure data gathering.
/// </summary>
public sealed class VendorCallEvidenceCollector
{
    private static readonly HashSet<DocType> ContractDocTypes =
    [
        DocType.SignedContract,
        DocType.ExecutedAgreement,
        DocType.Amendment,
        DocType.Addendum,
        DocType.PurchaseOrder,
    ];

    /// <summary>Sender local-parts that indicate automated/bulk mail (noise).</summary>
    private static readonly string[] NoiseLocalParts =
    [
        "events", "newsletter", "noreply", "no-reply", "donotreply", "do-not-reply",
        "marketing", "notifications", "notification", "alerts", "alert",
        "mailer-daemon", "bounce", "postmaster",
    ];

    private const int StaleDays = 4;

    private readonly IMailSource    _mailSource;
    private readonly IEntityStore   _entityStore;

    public VendorCallEvidenceCollector(IMailSource mailSource, IEntityStore entityStore)
    {
        _mailSource   = mailSource;
        _entityStore  = entityStore;
    }

    /// <summary>
    /// Collects all evidence relevant to the vendor call.
    /// <paramref name="now"/> is passed in — this class never reads the clock.
    /// </summary>
    public async Task<VendorCallEvidenceBundle> CollectAsync(
        VendorCallContext context,
        DateTimeOffset    now,
        CancellationToken ct = default)
    {
        var vendorId = context.Match.VendorId;

        // ── 1. Evidence + beliefs from entity store ───────────────────────────
        var allEvidence = await _entityStore.GetEvidenceForVendorAsync(vendorId, ct);
        var allBeliefs  = await _entityStore.GetCurrentBeliefsAsync(vendorId, ct);

        // Build the set of EvidenceIds that have at least one belief (via Provenance)
        var evidenceIdsWithBeliefs = allBeliefs
            .Where(b => b.Provenance is not null)
            .Select(b => b.Provenance!.EvidenceId)
            .ToHashSet();

        // ── 2. Classify evidence ──────────────────────────────────────────────
        var contracts       = new List<Evidence>();
        var priorNotes      = new List<Evidence>();
        var openCommitments = new List<Evidence>();
        var signals         = new List<Evidence>();

        foreach (var ev in allEvidence)
        {
            if (ContractDocTypes.Contains(ev.DocType))
                contracts.Add(ev);
            else if (ev.DocType == DocType.OwnerNote)
            {
                if (evidenceIdsWithBeliefs.Contains(ev.EvidenceId))
                    priorNotes.Add(ev);
                else
                    openCommitments.Add(ev);
            }
            else if (ev.DocType is DocType.Email or DocType.Communication)
                signals.Add(ev);
            // Other DocTypes (Invoice, UsageCsv, WebProfile, …) are silently ignored here
        }

        // ── 3. Fetch emails via mail source ───────────────────────────────────
        var criteria = new MailSearchCriteria(
            VendorId:            vendorId,
            VendorDomains:       context.VendorDomains,
            MeetingParticipants: context.Meeting.Attendees,
            FromUtc:             now.AddDays(-context.Recipe.EmailLookbackDays),
            ToUtc:               now,
            CommercialTerms:     [],
            MaximumMessages:     context.Recipe.Limits.MaximumEmails);

        var allEmails = await _mailSource.FindRelevantMessagesAsync(
            context.SignedInUserPrincipalId, criteria, ct);

        var (commercial, noise) = PartitionEmails(allEmails);

        // ── 4. Evidence gaps ──────────────────────────────────────────────────
        var gaps = new List<string>();

        if (contracts.Count == 0)
            gaps.Add("No signed contract on file for this vendor.");

        foreach (var commitment in openCommitments)
        {
            var ageDays = (now - commitment.IngestedAt).TotalDays;
            if (ageDays > StaleDays)
                gaps.Add(
                    $"Overdue open commitment: {commitment.Ref} " +
                    $"(age {(int)ageDays} days, threshold {StaleDays} days).");
        }

        if (!allBeliefs.Any(b => string.Equals(b.ClaimKey, "renewal_intent",
                                     StringComparison.OrdinalIgnoreCase)))
            gaps.Add("No renewal intent belief recorded for this vendor.");

        return new VendorCallEvidenceBundle(
            RecentEmails:        commercial,
            FilteredNoiseEmails: noise,
            Contracts:           contracts,
            PriorMeetingNotes:   priorNotes,
            OpenCommitments:     openCommitments,
            CommercialSignals:   signals,
            EvidenceGaps:        gaps);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static (IReadOnlyList<MailArtifact> commercial, IReadOnlyList<MailArtifact> noise)
        PartitionEmails(IReadOnlyList<MailArtifact> emails)
    {
        var commercial = new List<MailArtifact>();
        var noise      = new List<MailArtifact>();
        foreach (var email in emails)
            (IsNoise(email.Sender) ? noise : commercial).Add(email);
        return (commercial, noise);
    }

    private static bool IsNoise(string sender)
    {
        var at = sender.IndexOf('@');
        if (at < 0) return false;
        var local = sender[..at].ToLowerInvariant();
        return NoiseLocalParts.Any(p =>
            local.Equals(p, StringComparison.Ordinal) ||
            local.StartsWith(p + ".", StringComparison.Ordinal));
    }
}
