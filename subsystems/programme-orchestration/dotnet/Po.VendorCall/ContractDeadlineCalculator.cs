using Kozmo.Contracts;

namespace Po.VendorCall;

/// <summary>
/// Pure-math helper for renewal notice-deadline arithmetic.
/// Called by both Q1FactAssembler and Q4FactAssembler — the single source of truth
/// so the math can never drift between the two callers.
/// No LLM, no IO, no clock reads.
/// </summary>
public static class ContractDeadlineCalculator
{
    /// <summary>
    /// Extracts renewal_date and notice_period beliefs and computes the deadline.
    /// Returns null if either belief is absent.
    /// </summary>
    public static ContractDeadlineResult? ComputeFromBeliefs(
        IReadOnlyList<Belief> beliefs, DateTimeOffset today)
    {
        var renewalRaw   = beliefs.FirstOrDefault(b =>
            string.Equals(b.ClaimKey, "renewal_date", StringComparison.OrdinalIgnoreCase));
        var noticePeriod = beliefs.FirstOrDefault(b =>
            string.Equals(b.ClaimKey, "notice_period", StringComparison.OrdinalIgnoreCase));

        if (renewalRaw is null || noticePeriod is null) return null;

        return Compute(
            renewalDate:       DateTimeOffset.FromUnixTimeSeconds((long)renewalRaw.Value),
            noticePeriodDays:  (int)noticePeriod.Value,
            today:             today);
    }

    /// <summary>Core arithmetic — independently testable without beliefs.</summary>
    public static ContractDeadlineResult Compute(
        DateTimeOffset renewalDate, int noticePeriodDays, DateTimeOffset today)
    {
        var noticeDeadline    = renewalDate.AddDays(-noticePeriodDays);
        var daysUntilDeadline = (int)(noticeDeadline - today).TotalDays;
        return new ContractDeadlineResult(
            RenewalDate:         renewalDate.ToString("yyyy-MM-dd"),
            NoticeDeadline:      noticeDeadline.ToString("yyyy-MM-dd"),
            DaysUntilDeadline:   daysUntilDeadline);
    }
}

public sealed record ContractDeadlineResult(
    string RenewalDate,
    string NoticeDeadline,
    int    DaysUntilDeadline);
