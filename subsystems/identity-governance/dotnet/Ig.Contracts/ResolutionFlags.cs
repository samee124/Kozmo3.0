namespace Ig.Contracts;

/// <summary>
/// String constants for the flag library defined in §4 of the Identity Resolution Spec.
/// Flags are carried as IReadOnlyList&lt;string&gt; on CandidateCluster so Stage E / Stage F
/// can add further flags without a contract amendment.
/// </summary>
public static class ResolutionFlags
{
    public const string FuzzyMatch         = "FUZZY_MATCH";
    public const string AliasMatch         = "ALIAS_MATCH";
    public const string LowConfidenceMatch = "LOW_CONFIDENCE_MATCH";
    public const string AutoConfirmed      = "AUTO_CONFIRMED";
    public const string GenericName        = "GENERIC_NAME";
    public const string MultipleMatches    = "MULTIPLE_MATCHES";
    public const string PersonEntity       = "PERSON_ENTITY";
    public const string ProductEntity      = "PRODUCT_ENTITY";
    public const string InternalEntity     = "INTERNAL_ENTITY";
    public const string NonVendorEntity    = "NON_VENDOR_ENTITY";
    public const string Collision          = "COLLISION";
    public const string SuspectedRebrand  = "SUSPECTED_REBRAND";
    public const string WeakEvidence       = "WEAK_EVIDENCE";
    public const string SingleSourceOnly   = "SINGLE_SOURCE_ONLY";
    public const string ProvisionalVendor  = "PROVISIONAL_VENDOR";
    public const string TriageRequired     = "TRIAGE_REQUIRED";
    public const string PossibleSameEntity = "POSSIBLE_SAME_ENTITY";
}
