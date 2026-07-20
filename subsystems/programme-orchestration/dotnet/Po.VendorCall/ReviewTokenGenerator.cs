using System.Security.Cryptography;

namespace Po.VendorCall;

/// <summary>
/// Generates and validates the one-time review token stored on VendorCallRun.
/// The token is a 32-byte cryptographically random base64url-encoded string.
/// Expiry is 48 hours from generation.
/// </summary>
public static class ReviewTokenGenerator
{
    private const int TokenBytes = 32;
    private const int TtlHours   = 48;

    /// <summary>Generates a cryptographically random base64url token (no padding).</summary>
    public static string Generate()
    {
        var bytes = RandomNumberGenerator.GetBytes(TokenBytes);
        return Convert.ToBase64String(bytes)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }

    /// <summary>Returns the expiry time: 48 hours from the given origin.</summary>
    public static DateTimeOffset ExpiresAt(DateTimeOffset origin) =>
        origin.AddHours(TtlHours);
}
