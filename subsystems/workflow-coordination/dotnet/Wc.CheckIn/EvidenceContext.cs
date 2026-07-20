using Kozmo.Contracts;

namespace Wc.CheckIn;

/// <summary>
/// One piece of existing evidence for a (vendorId, claimKey) pair extracted from the belief store.
/// Used to build answer options and provide context for LLM question phrasing.
/// </summary>
public sealed record EvidenceEntry(
    /// <summary>Human-readable description. Shown in the email and in the LLM prompt.</summary>
    string     DisplayText,
    /// <summary>
    /// The original scoreable raw value extracted from the belief derivation (e.g. "99.5" from
    /// a check-in answer "Check-in answer to \"...\": 99.5"). Null when the pre-rubric value
    /// cannot be reconstructed — such entries still inform LLM phrasing but do not generate
    /// an option button with a signed token.
    /// </summary>
    string?    OptionValue,
    /// <summary>Source attribution: provenance locator, rubric criterion, or "on record".</summary>
    string     Source,
    SourceTier Tier,
    double     Confidence);

/// <summary>
/// Evidence gathered deterministically (no LLM) for a specific (vendorId, claimKey) pair
/// before sending a check-in. HasEvidence=false means no matching current beliefs exist;
/// the LLM phrasing and evidence-option generation are skipped in that case.
/// </summary>
public sealed record EvidenceContext(
    string                       ClaimKey,
    IReadOnlyList<EvidenceEntry> Entries,
    bool                         HasEvidence)
{
    /// <summary>Sentinel for "no evidence" — used as a safe default before the store is queried.</summary>
    public static EvidenceContext Empty { get; } = new("", Array.Empty<EvidenceEntry>(), HasEvidence: false);
}

/// <summary>
/// A selectable answer option shown in the check-in email.
/// Value-options (IsOpenInput=false, Value != null) carry the original scoreable raw value so
/// clicking them scores identically to typing the same value in the in-app form.
/// "Something else" and "Not sure" link to the pending queue.
/// </summary>
public sealed record AnswerOption(
    /// <summary>Display label (e.g. "99.5% (from contract)" or "Something else").</summary>
    string  Label,
    /// <summary>
    /// Scoreable value to embed in the signed token. Null for "Something else" and "Not sure"
    /// — both link to the pending queue rather than producing a token link.
    /// </summary>
    string? Value,
    /// <summary>True for "Something else" — links to the pending typed-input path.</summary>
    bool    IsOpenInput);
