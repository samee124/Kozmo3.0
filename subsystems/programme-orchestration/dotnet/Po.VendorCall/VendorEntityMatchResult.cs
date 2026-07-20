namespace Po.VendorCall;

/// <summary>How the vendor entity was resolved from an external attendee domain.</summary>
public enum VendorMatchType
{
    DomainExact,
    NameExact,
    Alias,
    Unmatched
}

/// <summary>Result of matching one external domain against the vendor registry.</summary>
public sealed record VendorEntityMatchResult(
    Guid            VendorId,
    string          VendorName,
    VendorMatchType MatchType,
    double          MatchScore);
