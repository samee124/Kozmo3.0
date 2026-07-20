using Kozmo.Contracts;

namespace Ii.CandidateExtraction;

/// <summary>
/// Infers source tier and confidence ceiling from a document filename.
/// Matches the tier logic used in the Phase 1 identity probe diagnostic.
/// </summary>
public static class DocTypeInferrer
{
    /// <summary>
    /// Returns the source tier implied by the document filename (case-insensitive).
    /// Convention mirrors the workspace naming used in Phase 1:
    ///   _signed / _executed / _final_ → Primary (of-record)
    ///   w9 / banking / insurance / certificate / ach / verification → Verified
    ///   draft / unsigned / meeting / email → Reported
    ///   anything else → Verified (safe default)
    /// </summary>
    public static SourceTier InferTier(string fileName)
    {
        var f = Path.GetFileNameWithoutExtension(fileName).ToLowerInvariant();

        if (f.Contains("_signed")   || f.Contains("_executed") || f.Contains("_final_") ||
            f.StartsWith("signed_") || f.EndsWith("_signed"))
            return SourceTier.Primary;

        if (f.Contains("draft") || f.Contains("unsigned") || f.Contains("meeting") ||
            f.Contains("email"))
            return SourceTier.Reported;

        // w9, banking, insurance, certificate, ach, verification all → Verified
        return SourceTier.Verified;
    }

    /// <summary>
    /// Per-tier confidence ceiling — matches the catalogue source_tiers config.
    /// These are the max confidence values the tier system allows; the extractor clamps to them.
    /// </summary>
    // E-signal Part 5 Step 1: Correspondence added at a value consistent with source_tiers.saas.v1.json
    // (0.25). The pre-existing non-monotonic drift among the other rows (Inferred > Reported here,
    // unlike source_tiers.json) is untouched — out of scope for this step.
    public static double TierCeiling(SourceTier tier) => tier switch
    {
        SourceTier.Primary        => 0.95,
        SourceTier.Verified       => 0.80,
        SourceTier.Reported       => 0.50,
        SourceTier.Inferred       => 0.60,
        SourceTier.Unverified     => 0.40,
        SourceTier.Correspondence => 0.25,
        SourceTier.Confirmed      => 0.65,
        _                         => 0.80,
    };

    /// <summary>
    /// E1 Part 7 Step 3: infers a document TYPE (distinct from tier) from filename, used to select
    /// an extraction schema (SaasProfile.ExtractionSchemas). Narrow by design — only the types
    /// wired to a distinct schema so far. Anything else returns "" (unclassified), which
    /// DocumentBeliefExtractor maps to the default (pre-E1) five-key schema — never a code change
    /// to extend, always a catalogue-config change (a new doc_type_schemas entry).
    /// </summary>
    public static string InferDocType(string fileName)
    {
        // E-signal Part 5 Step 6: by extension, not filename content — an email's filename
        // (e.g. "0006_pricing.eml") never contains the word "email". Checked first; never
        // collides with a document, since no .pdf carries a .eml extension.
        if (Path.GetExtension(fileName).Equals(".eml", StringComparison.OrdinalIgnoreCase))
            return "email";

        var f = Path.GetFileNameWithoutExtension(fileName).ToLowerInvariant().Replace('_', ' ');

        if (f.Contains("invoice"))                                  return "invoice";
        if (f.Contains("msa") || f.Contains("master service"))      return "msa";
        if (f.Contains("order form") || f.Contains("orderform"))    return "order_form";

        return "";
    }

    // Bare filename keywords, not the fully-derived word list InferDocType uses ("_" left in,
    // e.g. "ACH_Banking_Form" contains "ach" and "banking" either way) — deliberately separate
    // from InferDocType (which is narrow-by-design: only doc types wired to a distinct
    // extraction schema, see its own doc comment) since a banking-context signal has nothing to
    // do with claim-key extraction-schema selection; it exists solely to tell the identity
    // extraction prompt "any bank named in this document is a payment-routing detail, not a
    // vendor" (B3 — bank false-positive suppression).
    private static readonly string[] BankingContextKeywords =
        ["ach", "banking", "routing", "wire", "remittance", "direct deposit"];

    /// <summary>
    /// True when the filename indicates the document is an ACH/banking-details/wire-instruction
    /// form — i.e. any bank or financial institution named in it is providing payment-routing
    /// information, never acting as a vendor. Filename-based (cheap, deterministic), matching the
    /// existing InferTier/InferDocType convention — no document text is scanned.
    /// </summary>
    public static bool IsBankingContext(string fileName)
    {
        var f = Path.GetFileNameWithoutExtension(fileName).ToLowerInvariant().Replace('_', ' ');
        return BankingContextKeywords.Any(f.Contains);
    }
}
