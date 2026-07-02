using Ig.Contracts;

namespace Ig.Resolution;

/// <summary>
/// Shared signal-comparison helpers for Stage C and Stage D.
/// "Conflict" = both candidates supply a signal for the same field AND they disagree.
/// "Match"    = both candidates supply a signal for the same field AND they agree.
/// A null signal is never a conflict (absence of information is not a contradiction).
/// </summary>
internal static class SignalMatcher
{
    public static bool HasConflict(CandidateSignals? a, CandidateSignals? b)
    {
        if (a == null || b == null) return false;

        if (a.Domain != null && b.Domain != null &&
            !a.Domain.Equals(b.Domain, StringComparison.OrdinalIgnoreCase))
            return true;

        if (a.TaxId != null && b.TaxId != null &&
            !a.TaxId.Equals(b.TaxId, StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }

    public static bool HasMatch(CandidateSignals? a, CandidateSignals? b)
    {
        if (a == null || b == null) return false;

        if (a.Domain != null && b.Domain != null &&
            a.Domain.Equals(b.Domain, StringComparison.OrdinalIgnoreCase))
            return true;

        if (a.TaxId != null && b.TaxId != null &&
            a.TaxId.Equals(b.TaxId, StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }
}
