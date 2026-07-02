using System.Text;
using Ii.Contracts;
using Kozmo.Contracts;

namespace Ii.Intake;

/// <summary>
/// Pure formatting: takes a recomputed VendorJudgement + belief set + evidence and
/// emits the layered Vendor.md. No recompute, no writes, no clock reads.
/// </summary>
public static class VendorFileRenderer
{
    public static string Render(
        Guid vendorId,
        string vendorName,
        DateTimeOffset asOf,
        VendorJudgement judgement,
        IReadOnlyList<Belief> activeBeliefs,
        IReadOnlyList<Belief> allBeliefs,
        IReadOnlyList<Evidence> evidence)
    {
        var sb = new StringBuilder();
        AppendIdentity(sb, vendorId, vendorName, asOf, judgement);
        AppendJudgement(sb, judgement);
        AppendBeliefWorkingState(sb, activeBeliefs, asOf);
        AppendBeliefHistory(sb, allBeliefs);
        AppendEvidence(sb, evidence);
        AppendAnalysis(sb, judgement.Meta);
        AppendManagementBlock(sb, judgement.Management);
        AppendLedgers(sb);
        AppendNarrative(sb, vendorName, asOf, judgement);
        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine($"*Fingerprint: `{judgement.Index.Fingerprint}`*  ");
        sb.Append("*Rendered by Kozmo VendorFileRenderer — deterministic, no AI generation.*");
        return sb.ToString();
    }

    // ── Layer 1: Identity ─────────────────────────────────────────────────────

    private static void AppendIdentity(StringBuilder sb, Guid vendorId, string vendorName,
        DateTimeOffset asOf, VendorJudgement j)
    {
        sb.AppendLine($"# Vendor File — {vendorName}");
        sb.AppendLine();
        sb.AppendLine("## Identity");
        sb.AppendLine();
        sb.AppendLine("| Field | Value |");
        sb.AppendLine("|---|---|");
        sb.AppendLine($"| Vendor | {vendorName} |");
        sb.AppendLine($"| Vendor ID | `{vendorId}` |");
        sb.AppendLine($"| As of | {asOf:yyyy-MM-dd} |");
        sb.AppendLine($"| Band | {j.Index.Band} |");
        sb.AppendLine($"| Stance | {j.Posture.Stance} |");
        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine();
    }

    // ── Layer 2: Judgement ────────────────────────────────────────────────────

    private static void AppendJudgement(StringBuilder sb, VendorJudgement j)
    {
        sb.AppendLine("## Judgement");
        sb.AppendLine();
        sb.AppendLine($"**Posture:** {j.Posture.Stance} &ensp;|&ensp; **Band:** {j.Index.Band} &ensp;|&ensp; **Composite:** {j.Index.Composite:P0}  ");
        sb.AppendLine($"**Band driven by:** {j.Index.BandDrivenBy} &ensp;|&ensp; **Confidence floor:** {j.Index.ConfidenceFloor:F3}  ");
        sb.AppendLine();
        sb.AppendLine($"> {j.Posture.Rationale}");
        sb.AppendLine();
        sb.AppendLine("### Dimension Scores");
        sb.AppendLine();
        sb.AppendLine("| Dimension | Score | Confidence | Beliefs |");
        sb.AppendLine("|---|---|---|---|");
        foreach (var kv in j.Index.DimensionScores.OrderBy(kv => kv.Key.ToString()))
            sb.AppendLine($"| {kv.Key} | {kv.Value.Score:F3} | {kv.Value.Confidence:F3} | {kv.Value.ContributingBeliefIds.Count} |");
        sb.AppendLine();
        sb.AppendLine("### Coverage");
        sb.AppendLine();
        sb.AppendLine(j.Management.CoverageStatement);
        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine();
    }

    // ── Layer 3: Belief Working State ─────────────────────────────────────────

    private static void AppendBeliefWorkingState(StringBuilder sb,
        IReadOnlyList<Belief> beliefs, DateTimeOffset asOf)
    {
        sb.AppendLine("## Belief Working State");
        sb.AppendLine();
        sb.AppendLine($"Active claims as of {asOf:yyyy-MM-dd}.");
        sb.AppendLine();

        var vfBeliefs = beliefs
            .Where(b => !string.IsNullOrEmpty(b.ClaimKey))
            .OrderBy(b => b.ClaimKey)
            .ToList();

        if (vfBeliefs.Count == 0)
        {
            sb.AppendLine("_(no vendor file beliefs)_");
            sb.AppendLine();
            sb.AppendLine("---");
            sb.AppendLine();
            return;
        }

        sb.AppendLine("| Claim Key | Dimension | Value | Tier | Confidence | Ver | Provenance | Expires |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|");
        foreach (var b in vfBeliefs)
        {
            var dim     = b.Dimension?.ToString() ?? "—";
            var prov    = b.Provenance?.Locator ?? "—";
            var expires = b.ValidUntil.HasValue ? b.ValidUntil.Value.ToString("yyyy-MM-dd") : "—";
            sb.AppendLine($"| {b.ClaimKey} | {dim} | {FormatValue(b)} | {b.SourceTier} | {b.Confidence:F3} | {b.Version} | {prov} | {expires} |");
        }
        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine();
    }

    // ── Layer 4: Belief History ───────────────────────────────────────────────

    private static void AppendBeliefHistory(StringBuilder sb, IReadOnlyList<Belief> allBeliefs)
    {
        sb.AppendLine("## Belief History");
        sb.AppendLine();

        var superseded = allBeliefs
            .Where(b => b.SupersededBy != null && !string.IsNullOrEmpty(b.ClaimKey))
            .OrderBy(b => b.ClaimKey).ThenBy(b => b.Version)
            .ToList();

        if (superseded.Count == 0)
        {
            sb.AppendLine("_(no superseded beliefs)_");
            sb.AppendLine();
            sb.AppendLine("---");
            sb.AppendLine();
            return;
        }

        sb.AppendLine("| Claim Key | Value | Tier | Ver | Superseded By |");
        sb.AppendLine("|---|---|---|---|---|");
        foreach (var b in superseded)
            sb.AppendLine($"| {b.ClaimKey} | {FormatValue(b)} | {b.SourceTier} | {b.Version} | `{b.SupersededBy}` |");
        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine();
    }

    // ── Layer 5: Evidence ─────────────────────────────────────────────────────

    private static void AppendEvidence(StringBuilder sb, IReadOnlyList<Evidence> evidence)
    {
        sb.AppendLine("## Evidence");
        sb.AppendLine();

        if (evidence.Count == 0)
        {
            sb.AppendLine("_(no evidence records)_");
            sb.AppendLine();
            sb.AppendLine("---");
            sb.AppendLine();
            return;
        }

        foreach (var tier in new[] { SourceTier.Primary, SourceTier.Verified, SourceTier.Reported,
                                     SourceTier.Inferred, SourceTier.Unverified })
        {
            var group = evidence.Where(e => e.SourceTier == tier).ToList();
            if (group.Count == 0) continue;
            sb.AppendLine($"### {tier}");
            sb.AppendLine();
            sb.AppendLine("| Ref | Doc Type | Ingested |");
            sb.AppendLine("|---|---|---|");
            foreach (var e in group)
                sb.AppendLine($"| {e.Ref} | {e.DocType} | {e.IngestedAt:yyyy-MM-dd} |");
            sb.AppendLine();
        }
        sb.AppendLine("---");
        sb.AppendLine();
    }

    // ── Layer 6: Analysis ─────────────────────────────────────────────────────

    private static void AppendAnalysis(StringBuilder sb, MetaCognitionResult meta)
    {
        sb.AppendLine("## Analysis");
        sb.AppendLine();
        sb.AppendLine("### Contradictions");
        sb.AppendLine();
        if (meta.Contradictions.Count == 0)
        {
            sb.AppendLine("_(none detected)_");
        }
        else
        {
            sb.AppendLine("| Dimension | Severity | Description |");
            sb.AppendLine("|---|---|---|");
            foreach (var c in meta.Contradictions)
                sb.AppendLine($"| {c.Dimension} | {c.Severity} | {c.Description} |");
        }
        sb.AppendLine();
        sb.AppendLine("### Evidence Gaps");
        sb.AppendLine();
        if (meta.Gaps.Count == 0)
        {
            sb.AppendLine("_(no gaps detected)_");
        }
        else
        {
            sb.AppendLine("| Dimension | Description |");
            sb.AppendLine("|---|---|");
            foreach (var g in meta.Gaps)
                sb.AppendLine($"| {g.Dimension} | {g.Description} |");
        }
        sb.AppendLine();
        sb.AppendLine($"**Epistemic Summary:** {meta.EpistemicSummary}");
        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine();
    }

    // ── Layer 7: Management Block ─────────────────────────────────────────────

    private static void AppendManagementBlock(StringBuilder sb, ManagementBlock mgmt)
    {
        sb.AppendLine("## Management Block");
        sb.AppendLine();
        sb.AppendLine("| Field | Value |");
        sb.AppendLine("|---|---|");
        sb.AppendLine($"| Completeness | {mgmt.FilledCount}/{mgmt.ExpectedCount} ({mgmt.Completeness:P0}) |");
        sb.AppendLine($"| Gaps | {(mgmt.GapSlots.Count > 0 ? string.Join(", ", mgmt.GapSlots) : "—")} |");
        sb.AppendLine($"| Weak Dimensions | {(mgmt.WeakDimensions.Count > 0 ? string.Join(", ", mgmt.WeakDimensions) : "—")} |");
        sb.AppendLine($"| Renewal Deadline | {(mgmt.Flags.RenewalDeadline.HasValue ? mgmt.Flags.RenewalDeadline.Value.ToString("yyyy-MM-dd") : "—")} |");
        sb.AppendLine($"| Contradictions | {(mgmt.Flags.HasContradictions ? "Yes — see Analysis" : "None")} |");
        sb.AppendLine($"| Verification State | {mgmt.VerificationState} |");
        sb.AppendLine($"| Refresh Next Due | {(mgmt.Refresh.NextDue.HasValue ? mgmt.Refresh.NextDue.Value.ToString("yyyy-MM-dd") : "—")} |");
        sb.AppendLine();
        sb.AppendLine($"> {mgmt.CoverageStatement}");
        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine();
    }

    // ── Layer 8: Ledgers ──────────────────────────────────────────────────────

    private static void AppendLedgers(StringBuilder sb)
    {
        sb.AppendLine("## Ledgers");
        sb.AppendLine();
        sb.AppendLine("### Action Ledger");
        sb.AppendLine();
        sb.AppendLine("> No actions this phase.");
        sb.AppendLine();
        sb.AppendLine("### Outcome Ledger");
        sb.AppendLine();
        sb.AppendLine("> No outcomes this phase.");
        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine();
    }

    // ── Layer 9: Narrative ────────────────────────────────────────────────────

    private static void AppendNarrative(StringBuilder sb, string vendorName,
        DateTimeOffset asOf, VendorJudgement j)
    {
        sb.AppendLine("## Narrative");
        sb.AppendLine();

        var mgmt = j.Management;

        var weakStr = mgmt.WeakDimensions.Count > 0
            ? $"Weak in: {string.Join(", ", mgmt.WeakDimensions)}."
            : "No dimensions below the AtRisk threshold.";

        var gapStr = mgmt.GapSlots.Count > 0
            ? $"Blind spots: {string.Join(", ", mgmt.GapSlots)}."
            : "No evidence gaps.";

        var nextMove = j.Posture.Stance switch
        {
            Stance.Maintain    => "Continue as-is; schedule routine review.",
            Stance.Monitor     => "Increase monitoring cadence; request updated evidence.",
            Stance.Renegotiate => "Initiate contract review; prepare negotiation brief.",
            Stance.Escalate    => "Escalate to senior stakeholder; enforce SLA remediation.",
            Stance.Remediate   => "Immediate remediation required; convene crisis session.",
            _                  => "Review and determine next action."
        };

        sb.AppendLine($"**Kozmo assessment as of {asOf:yyyy-MM-dd}:**");
        sb.AppendLine();
        sb.AppendLine($"{vendorName} is currently rated **{j.Index.Band}** (composite {j.Index.Composite:P0}) with a **{j.Posture.Stance}** stance.");
        sb.AppendLine();
        sb.AppendLine($"Evidence completeness {mgmt.FilledCount}/{mgmt.ExpectedCount} ({mgmt.Completeness:P0}). {weakStr}");
        sb.AppendLine();
        sb.AppendLine(gapStr);
        sb.AppendLine();
        sb.AppendLine($"Recommended next move: {nextMove}");
        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string FormatValue(Belief b) =>
        b.Value switch
        {
            >= 10000 => b.Value.ToString("N0"),
            >= 100   => b.Value.ToString("F0"),
            >= 1.0   => b.Value.ToString("F2"),
            _        => b.Value.ToString("F3")
        };
}
