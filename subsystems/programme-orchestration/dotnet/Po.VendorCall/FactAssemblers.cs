using System.Globalization;
using Kozmo.Contracts;

namespace Po.VendorCall;

// Overdue threshold — intentionally the same constant as VendorCallEvidenceCollector.StaleDays
// so overdue logic is never computed differently in two places.
file static class StaleDaysConst { internal const int Value = 4; }

// ==========================================================
// Q1 — What are we trying to accomplish?
// ==========================================================

/// <summary>
/// Facts for Q1: the meeting's purpose, renewal deadline context, and prior answer.
/// </summary>
public sealed record Q1FactPacket(
    string                VendorName,
    string                MeetingTypePhrase,      // human-readable label for the meeting type
    string?               RenewalDate,            // "yyyy-MM-dd" or null if no contract
    string?               NoticeDeadline,         // "yyyy-MM-dd" or null
    int?                  DaysUntilDeadline,      // null if no contract
    string?               PreviousAnswer,         // previousCheckpoint?.Q1Answer
    bool                  IsFirstReview,
    IReadOnlyList<string> SourceReferenceIds);

public interface IQ1FactAssembler
{
    /// <param name="eventTypeCode">
    /// Caller-supplied meeting type code (e.g. "vendor_review", "renewal_discussion").
    /// VendorCallRecognitionResult has no EventType field, so the caller derives this
    /// from the meeting subject or recognition metadata.
    /// </param>
    Q1FactPacket Assemble(
        VendorCallEvidenceBundle  bundle,
        IReadOnlyList<Belief>     beliefs,
        ReviewCheckpoint?         previousCheckpoint,
        string                    vendorName,
        string                    eventTypeCode,
        DateTimeOffset            today);
}

public sealed class Q1FactAssembler : IQ1FactAssembler
{
    private static readonly Dictionary<string, string> EventTypePhrases =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["vendor_review"]       = "quarterly vendor review",
            ["renewal_discussion"]  = "renewal discussion",
            ["pricing_meeting"]     = "pricing review",
            ["service_review"]      = "service review",
            ["kickoff"]             = "project kickoff",
            ["escalation"]          = "escalation discussion",
            ["qbr"]                 = "quarterly business review",
        };

    public Q1FactPacket Assemble(
        VendorCallEvidenceBundle  bundle,
        IReadOnlyList<Belief>     beliefs,
        ReviewCheckpoint?         previousCheckpoint,
        string                    vendorName,
        string                    eventTypeCode,
        DateTimeOffset            today)
    {
        var meetingTypePhrase = EventTypePhrases.TryGetValue(eventTypeCode, out var phrase)
            ? phrase : "vendor meeting";

        var sourceIds = new List<string>();

        var deadline = ContractDeadlineCalculator.ComputeFromBeliefs(beliefs, today);

        if (deadline is not null)
        {
            var contractSource = bundle.Contracts
                .OrderByDescending(c => c.IngestedAt)
                .FirstOrDefault();
            if (contractSource is not null)
                sourceIds.Add(contractSource.EvidenceId.ToString());
        }

        return new Q1FactPacket(
            VendorName:           vendorName,
            MeetingTypePhrase:    meetingTypePhrase,
            RenewalDate:          deadline?.RenewalDate,
            NoticeDeadline:       deadline?.NoticeDeadline,
            DaysUntilDeadline:    deadline?.DaysUntilDeadline,
            PreviousAnswer:       previousCheckpoint?.Q1Answer,
            IsFirstReview:        previousCheckpoint is null,
            SourceReferenceIds:   sourceIds);
    }
}

// ==========================================================
// Q2 — What is our current / contemplated position?
// ==========================================================

/// <summary>One active or historical contract extracted from evidence + beliefs.</summary>
public sealed record ContractFact(
    string   Type,              // e.g. "SignedContract"
    decimal? AnnualValue,       // from "annual_value" belief, null if not recorded
    string?  Currency,          // "GBP" if annual_value belief present, else null
    string   RenewalDate,       // "yyyy-MM-dd" from "renewal_date" belief, "" if absent
    int?     NoticePeriodDays,  // from "notice_period" belief
    string   SourceId);         // EvidenceId of the contract Evidence record

/// <summary>One open (unresolved) OwnerNote commitment.</summary>
public sealed record CommitmentFact(
    string  Description,        // derived from Evidence.Ref via label extraction
    string? Owner,              // not stored in Evidence — always null in this model
    string? DueDate,            // implicit: IngestedAt + StaleDays; formatted "yyyy-MM-dd"
    bool    IsOverdue,
    int?    OverdueDays,        // days past the stale threshold if overdue
    string  SourceId);

/// <summary>One commercial signal (Email/Communication evidence).</summary>
public sealed record SignalFact(
    string Description,
    string SourceId);

public sealed record Q2FactPacket(
    string                      VendorName,
    IReadOnlyList<ContractFact> Contracts,
    IReadOnlyList<CommitmentFact> OpenCommitments,
    IReadOnlyList<SignalFact>   CommercialSignals,
    IReadOnlyList<string>       EvidenceGaps,
    /// <summary>Free-text context notes submitted via the pre-meeting "Post an update" page.</summary>
    IReadOnlyList<string>       UpdateNotes,
    string?                     PreviousAnswer,
    bool                        IsFirstReview,
    IReadOnlyList<string>       SourceReferenceIds);

public interface IQ2FactAssembler
{
    Q2FactPacket Assemble(
        VendorCallEvidenceBundle         bundle,
        IReadOnlyList<Belief>            beliefs,
        ReviewCheckpoint?                previousCheckpoint,
        DateTimeOffset                   now,
        IReadOnlyList<VendorUpdateNote>? updateNotes = null);
}

public sealed class Q2FactAssembler : IQ2FactAssembler
{
    public Q2FactPacket Assemble(
        VendorCallEvidenceBundle         bundle,
        IReadOnlyList<Belief>            beliefs,
        ReviewCheckpoint?                previousCheckpoint,
        DateTimeOffset                   now,
        IReadOnlyList<VendorUpdateNote>? updateNotes = null)
    {
        var sourceIds = new List<string>();

        // ── Contracts ─────────────────────────────────────────────────────────
        var annualValueBelief = beliefs.FirstOrDefault(b =>
            string.Equals(b.ClaimKey, "annual_value", StringComparison.OrdinalIgnoreCase));
        var renewalDateBelief = beliefs.FirstOrDefault(b =>
            string.Equals(b.ClaimKey, "renewal_date", StringComparison.OrdinalIgnoreCase));
        var noticePeriodBelief = beliefs.FirstOrDefault(b =>
            string.Equals(b.ClaimKey, "notice_period", StringComparison.OrdinalIgnoreCase));

        var renewalDateStr = renewalDateBelief is not null
            ? DateTimeOffset.FromUnixTimeSeconds((long)renewalDateBelief.Value).ToString("yyyy-MM-dd")
            : "";

        var contracts = bundle.Contracts
            .Select(c =>
            {
                sourceIds.Add(c.EvidenceId.ToString());
                return new ContractFact(
                    Type:            c.DocType.ToString(),
                    AnnualValue:     annualValueBelief is not null
                                         ? (decimal?)annualValueBelief.Value : null,
                    Currency:        annualValueBelief is not null ? "GBP" : null,
                    RenewalDate:     renewalDateStr,
                    NoticePeriodDays: noticePeriodBelief is not null
                                         ? (int?)noticePeriodBelief.Value : null,
                    SourceId:        c.EvidenceId.ToString());
            })
            .ToList();

        // ── Open commitments ──────────────────────────────────────────────────
        var commitments = bundle.OpenCommitments
            .Select(c =>
            {
                sourceIds.Add(c.EvidenceId.ToString());
                var ageDays    = (now - c.IngestedAt).TotalDays;
                var isOverdue  = ageDays > StaleDaysConst.Value;
                var overdueDays = isOverdue ? (int)(ageDays - StaleDaysConst.Value) : (int?)null;
                var dueDate    = c.IngestedAt.AddDays(StaleDaysConst.Value).ToString("yyyy-MM-dd");
                return new CommitmentFact(
                    Description: LabelFromRef(c.Ref),
                    Owner:       null,
                    DueDate:     dueDate,
                    IsOverdue:   isOverdue,
                    OverdueDays: overdueDays,
                    SourceId:    c.EvidenceId.ToString());
            })
            .ToList();

        // ── Commercial signals ────────────────────────────────────────────────
        var signals = bundle.CommercialSignals
            .Select(s =>
            {
                sourceIds.Add(s.EvidenceId.ToString());
                return new SignalFact(
                    Description: LabelFromRef(s.Ref),
                    SourceId:    s.EvidenceId.ToString());
            })
            .ToList();

        var noteStrings = updateNotes is not null && updateNotes.Count > 0
            ? updateNotes
                .Select(n => $"User update ({n.SubmittedAtUtc:yyyy-MM-dd}): {n.NoteText.Trim()}")
                .ToList()
            : (IReadOnlyList<string>)[];

        return new Q2FactPacket(
            VendorName:        "", // caller should set if needed; Q2 is position-focused not name-focused
            Contracts:         contracts,
            OpenCommitments:   commitments,
            CommercialSignals: signals,
            EvidenceGaps:      bundle.EvidenceGaps,
            UpdateNotes:       noteStrings,
            PreviousAnswer:    previousCheckpoint?.Q2Answer,
            IsFirstReview:     previousCheckpoint is null,
            SourceReferenceIds: sourceIds.Distinct().ToList());
    }

    private static string LabelFromRef(string refPath)
    {
        var name  = Path.GetFileNameWithoutExtension(refPath);
        var parts = name.Split('-');
        var label = new List<string>();
        for (var i = 0; i < parts.Length; i++)
        {
            var p = parts[i];
            if (p.Length == 4 && int.TryParse(p, out var yr) && yr > 2000) break;
            if (p.Length == 2 && int.TryParse(p, out _) && i > 3) break;
            if (i == 0 && p.Equals("northstar", StringComparison.OrdinalIgnoreCase)) continue;
            if (p.Equals("commitment", StringComparison.OrdinalIgnoreCase)) continue;
            label.Add(p);
        }
        var result = string.Join(" ", label);
        return string.IsNullOrWhiteSpace(result) ? Path.GetFileName(refPath) : result;
    }
}

// ==========================================================
// Q3 — What is helping, preventing, or changing progress?
// ==========================================================

public sealed record Q3FactPacket(
    IReadOnlyList<string> HelpingFacts,      // short factual statements + source IDs embedded
    IReadOnlyList<string> PreventingFacts,
    IReadOnlyList<string> ChangingFacts,
    IReadOnlyList<string> SourceReferenceIds);

public interface IQ3FactAssembler
{
    Q3FactPacket Assemble(
        VendorCallEvidenceBundle bundle,
        IReadOnlyList<Belief>    beliefs,
        ReviewCheckpoint?        previousCheckpoint,
        DateTimeOffset           now);
}

public sealed class Q3FactAssembler : IQ3FactAssembler
{
    public Q3FactPacket Assemble(
        VendorCallEvidenceBundle bundle,
        IReadOnlyList<Belief>    beliefs,
        ReviewCheckpoint?        previousCheckpoint,
        DateTimeOffset           now)
    {
        var helping   = new List<string>();
        var preventing = new List<string>();
        var changing  = new List<string>();
        var sourceIds = new List<string>();

        var currentOverdue = bundle.OpenCommitments
            .Where(c => (now - c.IngestedAt).TotalDays > StaleDaysConst.Value)
            .ToList();
        var currentOverdueCount = currentOverdue.Count;
        var currentOpenCount    = bundle.OpenCommitments.Count;

        // ── Helping ───────────────────────────────────────────────────────────
        if (previousCheckpoint is not null &&
            currentOverdueCount < previousCheckpoint.OverdueCommitmentCount)
        {
            var resolved = previousCheckpoint.OverdueCommitmentCount - currentOverdueCount;
            helping.Add(
                $"{resolved} previously-overdue commitment(s) resolved since last review.");
        }

        if (currentOverdueCount == 0 && bundle.OpenCommitments.Count == 0)
            helping.Add("No open or overdue commitments outstanding.");

        var deadline = ContractDeadlineCalculator.ComputeFromBeliefs(beliefs, now);
        if (deadline is not null && deadline.DaysUntilDeadline > 30)
            helping.Add(
                $"Renewal notice deadline is {deadline.DaysUntilDeadline} days away — not yet critical.");

        // ── Preventing ────────────────────────────────────────────────────────
        foreach (var commitment in currentOverdue)
        {
            var ageDays = (int)(now - commitment.IngestedAt).TotalDays;
            sourceIds.Add(commitment.EvidenceId.ToString());
            preventing.Add(
                $"Commitment '{LabelFromRef(commitment.Ref)}' is {ageDays - StaleDaysConst.Value} " +
                $"day(s) past stale threshold (source: {commitment.EvidenceId}).");
        }

        foreach (var signal in bundle.CommercialSignals)
        {
            sourceIds.Add(signal.EvidenceId.ToString());
            preventing.Add(
                $"Unaddressed commercial signal: '{LabelFromRef(signal.Ref)}' " +
                $"(source: {signal.EvidenceId}).");
        }

        if (deadline is not null && deadline.DaysUntilDeadline <= 30)
        {
            preventing.Add(
                $"Renewal notice deadline is {deadline.DaysUntilDeadline} day(s) away " +
                $"({deadline.NoticeDeadline}) — window is closing.");
        }

        // ── Changing (vs previous checkpoint) ─────────────────────────────────
        if (previousCheckpoint is not null)
        {
            if (currentOpenCount != previousCheckpoint.OpenCommitmentCount)
            {
                var delta = currentOpenCount - previousCheckpoint.OpenCommitmentCount;
                changing.Add(
                    delta > 0
                        ? $"Open commitment count increased by {delta} since last review."
                        : $"Open commitment count decreased by {Math.Abs(delta)} since last review.");
            }

            if (currentOverdueCount != previousCheckpoint.OverdueCommitmentCount)
            {
                var delta = currentOverdueCount - previousCheckpoint.OverdueCommitmentCount;
                changing.Add(
                    delta > 0
                        ? $"Overdue commitment count increased by {delta} since last review."
                        : $"Overdue commitment count decreased by {Math.Abs(delta)} since last review.");
            }

            // New commitments since last checkpoint (by count only — no historical list available)
            var newCommitments = currentOpenCount - previousCheckpoint.OpenCommitmentCount;
            if (newCommitments > 0)
                changing.Add($"{newCommitments} new commitment(s) recorded since last review.");
        }

        return new Q3FactPacket(
            HelpingFacts:      helping,
            PreventingFacts:   preventing,
            ChangingFacts:     changing,
            SourceReferenceIds: sourceIds.Distinct().ToList());
    }

    private static string LabelFromRef(string refPath)
    {
        var name  = Path.GetFileNameWithoutExtension(refPath);
        var parts = name.Split('-');
        var label = new List<string>();
        for (var i = 0; i < parts.Length; i++)
        {
            var p = parts[i];
            if (p.Length == 4 && int.TryParse(p, out var yr) && yr > 2000) break;
            if (p.Length == 2 && int.TryParse(p, out _) && i > 3) break;
            if (i == 0 && p.Equals("northstar", StringComparison.OrdinalIgnoreCase)) continue;
            if (p.Equals("commitment", StringComparison.OrdinalIgnoreCase)) continue;
            label.Add(p);
        }
        var result = string.Join(" ", label);
        return string.IsNullOrWhiteSpace(result) ? Path.GetFileName(refPath) : result;
    }
}

// ==========================================================
// Q4 — What matters most now?
// ==========================================================

/// <summary>One ranked priority item, ordered most-urgent first.</summary>
public sealed record RankedPriority(
    string Description,
    string UrgencyReason,
    string SourceId);

public sealed record Q4FactPacket(
    IReadOnlyList<RankedPriority> TopPriorities,
    IReadOnlyList<string>         SourceReferenceIds);

public interface IQ4FactAssembler
{
    Q4FactPacket Assemble(
        VendorCallEvidenceBundle bundle,
        IReadOnlyList<Belief>    beliefs,
        DateTimeOffset           now,
        int                      maxPriorities = 3);
}

public sealed class Q4FactAssembler : IQ4FactAssembler
{
    public Q4FactPacket Assemble(
        VendorCallEvidenceBundle bundle,
        IReadOnlyList<Belief>    beliefs,
        DateTimeOffset           now,
        int                      maxPriorities = 3)
    {
        var priorities = new List<RankedPriority>();
        var sourceIds  = new List<string>();

        // 1. Overdue commitments — sorted by age descending (most overdue first)
        var overdueCommitments = bundle.OpenCommitments
            .Select(c => (commitment: c, ageDays: (now - c.IngestedAt).TotalDays))
            .Where(x => x.ageDays > StaleDaysConst.Value)
            .OrderByDescending(x => x.ageDays)
            .ToList();

        foreach (var (commitment, ageDays) in overdueCommitments)
        {
            if (priorities.Count >= maxPriorities) break;
            sourceIds.Add(commitment.EvidenceId.ToString());
            priorities.Add(new RankedPriority(
                Description:   $"Resolve overdue commitment: {LabelFromRef(commitment.Ref)}",
                UrgencyReason: $"Commitment is {(int)ageDays - StaleDaysConst.Value} day(s) past the stale threshold.",
                SourceId:      commitment.EvidenceId.ToString()));
        }

        // 2. Unresolved commercial signals (no response evidence yet)
        foreach (var signal in bundle.CommercialSignals)
        {
            if (priorities.Count >= maxPriorities) break;
            sourceIds.Add(signal.EvidenceId.ToString());
            priorities.Add(new RankedPriority(
                Description:   $"Respond to commercial signal: {LabelFromRef(signal.Ref)}",
                UrgencyReason: "Commercial signal has no documented counter-response.",
                SourceId:      signal.EvidenceId.ToString()));
        }

        // 3. Approaching renewal deadline — uses ContractDeadlineCalculator (same math as Q1)
        if (priorities.Count < maxPriorities)
        {
            var deadline = ContractDeadlineCalculator.ComputeFromBeliefs(beliefs, now);
            if (deadline is not null && deadline.DaysUntilDeadline <= 30)
            {
                var contractSource = bundle.Contracts
                    .OrderByDescending(c => c.IngestedAt)
                    .FirstOrDefault();
                var srcId = contractSource?.EvidenceId.ToString() ?? "renewal_deadline";
                if (contractSource is not null) sourceIds.Add(srcId);

                priorities.Add(new RankedPriority(
                    Description:   $"Confirm renewal position before notice deadline {deadline.NoticeDeadline}",
                    UrgencyReason: $"{deadline.DaysUntilDeadline} day(s) remaining — auto-renewal may trigger.",
                    SourceId:      srcId));
            }
        }

        return new Q4FactPacket(
            TopPriorities:      priorities,
            SourceReferenceIds: sourceIds.Distinct().ToList());
    }

    private static string LabelFromRef(string refPath)
    {
        var name  = Path.GetFileNameWithoutExtension(refPath);
        var parts = name.Split('-');
        var label = new List<string>();
        for (var i = 0; i < parts.Length; i++)
        {
            var p = parts[i];
            if (p.Length == 4 && int.TryParse(p, out var yr) && yr > 2000) break;
            if (p.Length == 2 && int.TryParse(p, out _) && i > 3) break;
            if (i == 0 && p.Equals("northstar", StringComparison.OrdinalIgnoreCase)) continue;
            if (p.Equals("commitment", StringComparison.OrdinalIgnoreCase)) continue;
            label.Add(p);
        }
        var result = string.Join(" ", label);
        return string.IsNullOrWhiteSpace(result) ? Path.GetFileName(refPath) : result;
    }
}

// ==========================================================
// Q5 — What should happen next?
// ==========================================================

/// <summary>One concrete recommended action derived from a Q4 priority.</summary>
public sealed record ActionItem(
    string  Action,
    string? Owner,
    string? DueDate,   // "yyyy-MM-dd" or null
    string  Effect,
    string  SourceId);

public sealed record Q5FactPacket(
    IReadOnlyList<ActionItem> RecommendedActions,
    IReadOnlyList<string>     SourceReferenceIds);

public interface IQ5FactAssembler
{
    Q5FactPacket Assemble(
        VendorCallEvidenceBundle bundle,
        Q4FactPacket             q4Packet,
        string                   ownerUpn,
        IReadOnlyList<Belief>    beliefs,
        DateTimeOffset           now);
}

public sealed class Q5FactAssembler : IQ5FactAssembler
{
    public Q5FactPacket Assemble(
        VendorCallEvidenceBundle bundle,
        Q4FactPacket             q4Packet,
        string                   ownerUpn,
        IReadOnlyList<Belief>    beliefs,
        DateTimeOffset           now)
    {
        var actions   = new List<ActionItem>();
        var sourceIds = new List<string>();

        foreach (var priority in q4Packet.TopPriorities)
        {
            // Renewal deadline → the one allowed synthetic action
            if (priority.Description.StartsWith("Confirm renewal position",
                    StringComparison.OrdinalIgnoreCase))
            {
                var deadline = ContractDeadlineCalculator.ComputeFromBeliefs(beliefs, now);
                actions.Add(new ActionItem(
                    Action:  "Confirm renewal or non-renewal position",
                    Owner:   ownerUpn,
                    DueDate: deadline?.NoticeDeadline,
                    Effect:  "Avoid unintended auto-renewal.",
                    SourceId: priority.SourceId));
            }
            else
            {
                // Map commitment/signal priorities to concrete actions
                var commitment = bundle.OpenCommitments
                    .FirstOrDefault(c => c.EvidenceId.ToString() == priority.SourceId);

                if (commitment is not null)
                {
                    var ageDays = (int)(now - commitment.IngestedAt).TotalDays;
                    actions.Add(new ActionItem(
                        Action:  $"Chase resolution of: {LabelFromRef(commitment.Ref)}",
                        Owner:   ownerUpn,
                        DueDate: null,
                        Effect:  $"Remove blocker — commitment has been open for {ageDays} day(s).",
                        SourceId: commitment.EvidenceId.ToString()));
                }
                else
                {
                    // Commercial signal
                    var signal = bundle.CommercialSignals
                        .FirstOrDefault(s => s.EvidenceId.ToString() == priority.SourceId);
                    if (signal is not null)
                    {
                        actions.Add(new ActionItem(
                            Action:  $"Request written response to: {LabelFromRef(signal.Ref)}",
                            Owner:   ownerUpn,
                            DueDate: null,
                            Effect:  "Document vendor's position before meeting.",
                            SourceId: signal.EvidenceId.ToString()));
                    }
                }
            }

            sourceIds.Add(priority.SourceId);
        }

        return new Q5FactPacket(
            RecommendedActions:  actions,
            SourceReferenceIds:  sourceIds.Distinct().ToList());
    }

    private static string LabelFromRef(string refPath)
    {
        var name  = Path.GetFileNameWithoutExtension(refPath);
        var parts = name.Split('-');
        var label = new List<string>();
        for (var i = 0; i < parts.Length; i++)
        {
            var p = parts[i];
            if (p.Length == 4 && int.TryParse(p, out var yr) && yr > 2000) break;
            if (p.Length == 2 && int.TryParse(p, out _) && i > 3) break;
            if (i == 0 && p.Equals("northstar", StringComparison.OrdinalIgnoreCase)) continue;
            if (p.Equals("commitment", StringComparison.OrdinalIgnoreCase)) continue;
            label.Add(p);
        }
        var result = string.Join(" ", label);
        return string.IsNullOrWhiteSpace(result) ? Path.GetFileName(refPath) : result;
    }
}
