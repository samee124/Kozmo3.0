namespace Po.VendorCall;


public interface IReviewStatusClassifier
{
    ReviewStatus    ClassifyStatus(VendorCallEvidenceBundle bundle, Q1FactPacket q1);
    ReviewMovement  ClassifyMovement(
        int currentOpenCount, int currentOverdueCount,
        ReviewCheckpoint? previousCheckpoint);
    ReviewConfidence ClassifyConfidence(VendorCallEvidenceBundle bundle);
}

public sealed class ReviewStatusClassifier : IReviewStatusClassifier
{
    private const int StaleDays        = 4;
    private const int SevereOverdueDays = 10;

    public ReviewStatus ClassifyStatus(VendorCallEvidenceBundle bundle, Q1FactPacket q1)
    {
        // ── Red triggers ──────────────────────────────────────────────────────
        // Any commitment overdue by >= 10 days past the stale threshold
        var maxOverdueDays = bundle.OpenCommitments
            .Select(c => c.IngestedAt)
            .DefaultIfEmpty()
            .Max();  
        var severelyOverdue = bundle.EvidenceGaps.Any(g =>
        {
            if (!g.StartsWith("Overdue open commitment:", StringComparison.OrdinalIgnoreCase))
                return false;
            // Format: "Overdue open commitment: {Ref} (age {N} days, threshold {T} days)."
            var ageStart = g.IndexOf("age ", StringComparison.OrdinalIgnoreCase);
            if (ageStart < 0) return false;
            var ageEnd = g.IndexOf(" days", ageStart + 4, StringComparison.OrdinalIgnoreCase);
            if (ageEnd < 0) return false;
            if (!int.TryParse(g.AsSpan(ageStart + 4, ageEnd - ageStart - 4), out var age))
                return false;
            return age >= StaleDays + SevereOverdueDays;
        });

        if (severelyOverdue) return ReviewStatus.Red;

        // Notice deadline < 7 days with no confirmed renewal position
        if (q1.DaysUntilDeadline is not null && q1.DaysUntilDeadline < 7)
        {
            return ReviewStatus.Red;
        }

        // No contract found → treat as critical evidence gap → Red
        if (bundle.Contracts.Count == 0 && bundle.EvidenceGaps.Any(g =>
                g.Contains("No signed contract", StringComparison.OrdinalIgnoreCase)))
        {
            // Only Red if renewal deadline is also imminent (< 7 days) or no deadline info
            // (without a contract we cannot compute the deadline, so cannot assess imminence)
            if (q1.DaysUntilDeadline is null || q1.DaysUntilDeadline < 7)
                return ReviewStatus.Red;
        }

        // ── Amber triggers ─────────────────────────────────────────────────────
        // Any overdue commitment (< 10 days past stale)
        if (bundle.EvidenceGaps.Any(g =>
                g.StartsWith("Overdue open commitment:", StringComparison.OrdinalIgnoreCase)))
            return ReviewStatus.Amber;

        // Any unresolved commercial signal
        if (bundle.CommercialSignals.Count > 0) return ReviewStatus.Amber;

        // Notice deadline < 30 days
        if (q1.DaysUntilDeadline is not null && q1.DaysUntilDeadline < 30)
            return ReviewStatus.Amber;

        return ReviewStatus.Green;
    }

    public ReviewMovement ClassifyMovement(
        int currentOpenCount, int currentOverdueCount,
        ReviewCheckpoint? previousCheckpoint)
    {
        // First review — always Stable (nothing to compare against)
        if (previousCheckpoint is null) return ReviewMovement.Stable;

        var prevOpen   = previousCheckpoint.OpenCommitmentCount;
        var prevOverdue = previousCheckpoint.OverdueCommitmentCount;

        // Improving: overdue count decreased AND open count did not increase
        if (currentOverdueCount < prevOverdue && currentOpenCount <= prevOpen)
            return ReviewMovement.Improving;

        // Weakening: overdue count increased OR open count increased
        if (currentOverdueCount > prevOverdue || currentOpenCount > prevOpen)
            return ReviewMovement.Weakening;

        return ReviewMovement.Stable;
    }

    public ReviewConfidence ClassifyConfidence(VendorCallEvidenceBundle bundle)
    {
        var hasContract = bundle.Contracts.Count > 0;
        var hasDirectEvidence = bundle.CommercialSignals.Count > 0
                             || bundle.OpenCommitments.Count > 0
                             || bundle.RecentEmails.Count > 0;

        // High: contract found AND at least one direct evidence item
        if (hasContract && hasDirectEvidence) return ReviewConfidence.High;

        // Medium: contract found but mostly gaps
        if (hasContract) return ReviewConfidence.Medium;

        // Low: no contract, or mostly empty bundle
        return ReviewConfidence.Low;
    }
}
