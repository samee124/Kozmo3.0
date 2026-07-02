namespace Ii.CandidateExtraction;

/// <summary>
/// Configuration for <see cref="CandidateFilter"/>. All lists are extensible so new document
/// types can be supported without touching filter logic.
/// </summary>
public sealed record CandidateFilterConfig(
    /// <summary>
    /// Strings that, when found at the START of a raw candidate name, identify a
    /// document-structure prefix that should be stripped. Matching is case-insensitive.
    /// The filter tries longest prefix first — keep multi-word entries before single-word.
    /// Strip yields the remainder after the prefix; if remainder is non-empty and starts with
    /// a real-name word (not in <see cref="GenericLeadWords"/>), the outcome is PrefixStripped.
    /// </summary>
    IReadOnlyList<string> StructurePrefixes,

    /// <summary>
    /// Strings whose presence at the START of a raw candidate name means the whole candidate
    /// is form/checkbox/table junk — always dropped, no strip attempted.
    /// </summary>
    IReadOnlyList<string> JunkStartsWithPatterns,

    /// <summary>
    /// Words that, when they are the FIRST word of the remainder after a structure-prefix strip,
    /// indicate the remainder is still junk (e.g. a table column label, not an org name).
    /// Checked case-insensitively against the first word of the stripped remainder.
    /// </summary>
    IReadOnlyList<string> GenericLeadWords
)
{
    /// <summary>
    /// Default configuration: covers the document-type labels, field labels, form artifacts,
    /// and table-header terms found in the Phase 1 diagnostic (28 PDFs, 4 scenarios).
    /// </summary>
    public static readonly CandidateFilterConfig Default = new(

        StructurePrefixes:
        [
            // ── Multi-word prefixes first (longest match wins) ─────────────────

            // Field labels from ACH / banking / W9 form templates
            "INFORMATION LEGAL NAME",
            "INFORMATION LEGAL",
            "OVERVIEW LEGAL NAME",
            "OVERVIEW LEGAL",
            "CHECKING ACCOUNT NAME",
            "ACCOUNT TYPE BUSINESS CHECKING ACCOUNT NAME",
            "ACCOUNT TYPE BUSINESS CHECKING",
            "ACCOUNT TYPE",
            "VENDOR IDENTIFICATION LEGAL ENTITY NAME",
            "VENDOR IDENTIFICATION",
            "LEGAL NAME",

            // Two-column MSA / agreement header artifacts
            "FROM BILL TO",
            "BILL TO",
            "VENDOR CLIENT N",
            "VENDOR CLIENT",

            // Document-type labels (linearised PDF header = type + entity name)
            "MASTER SERVICES AGREEMENT",
            "BANKING FORM",
            "TAX FORM",
            "ACH FORM",
            "VENDOR PROFILE",
            "CERTIFICATE",
            "AMENDMENT",
            "INVOICE",
            "QBR",
            "W9",

            // Insurance certificate / form section labels
            "INSURED INSURER",
            "CERTIFICATE HOLDER",
            "LLC AUTHORIZES",
            "LLC AND",

            // Proposal / brochure section headings
            "EXECUTIVE SUMMARY",
            "SIGNATURES PENDING N MASTER SERVICES AGREEMENT",
            "SIGNATURES PENDING",
            "WORK AND MASTER SERVICES AGREEMENT",
            "WORK AND",
            "SUBMITTED TO",
            "SUBMITTED BY",
            "ABOUT",

            // Single-word column headers (last — most risk of false-positive)
            "CLIENT",
            "OVERVIEW",
        ],

        JunkStartsWithPatterns:
        [
            // W9 form option text
            "FEDERAL TAX CLASSIFICATION",
            "NONPROFIT CORPORATION",
            "NONPROFIT",
            // Meeting-note table row artifacts — always pure junk, nothing real can follow
            "ATTENDEES NAME ORGANISATION ROLE",
            "ATTENDEES NAME",
            "ATTENDEES",
            "ORGANISATION ROLE",
        ],

        GenericLeadWords:
        [
            // Words that, after a structure prefix is stripped, still indicate junk
            // (the candidate is a table row or field label, not a clean org name)
            "Name",
            "Organisation",
            "Organization",
            "Role",
            "Type",
            "Classification",
            "Individual",
            "Business",
            "Account",
            "Information",
            "Overview",
            "Project",
            "Manager",
            "Analyst",
            "Director",
            "Coordinator",
            "Specialist",
            "Attendees",
        ]
    );
}
