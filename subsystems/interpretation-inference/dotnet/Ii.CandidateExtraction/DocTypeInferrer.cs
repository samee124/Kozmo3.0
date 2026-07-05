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
        var f = Path.GetFileNameWithoutExtension(fileName).ToLowerInvariant().Replace('_', ' ');

        if (f.Contains("invoice"))                                  return "invoice";
        if (f.Contains("msa") || f.Contains("master service"))      return "msa";
        if (f.Contains("order form") || f.Contains("orderform"))    return "order_form";

        return "";
    }
}
