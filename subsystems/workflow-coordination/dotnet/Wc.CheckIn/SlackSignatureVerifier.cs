using System.Security.Cryptography;
using System.Text;

namespace Wc.CheckIn;

/// <summary>
/// Verifies Slack request signatures per Slack's signing-secret scheme.
///
/// Base string: "v0:{X-Slack-Request-Timestamp}:{rawBody}"
/// Signature:   "v0=" + HMACSHA256(base, signingSecret) as lowercase hex
///
/// Security properties:
///   - Constant-time comparison (CryptographicOperations.FixedTimeEquals) prevents timing attacks.
///   - Timestamp replay guard: rejects requests older than 5 minutes (or in the future by >5 min).
///   - Any missing header returns false immediately.
/// </summary>
public static class SlackSignatureVerifier
{
    private const int ReplayWindowSeconds = 300; // 5 minutes

    /// <summary>
    /// Returns true when the signature is valid and the timestamp is within the replay window.
    /// </summary>
    public static bool Verify(
        string  rawBody,
        string? timestampHeader,
        string? signatureHeader,
        string  signingSecret)
    {
        if (string.IsNullOrWhiteSpace(timestampHeader) ||
            string.IsNullOrWhiteSpace(signatureHeader) ||
            string.IsNullOrWhiteSpace(signingSecret))
            return false;

        // Replay guard — reject stale or future-dated requests.
        if (!long.TryParse(timestampHeader, out var ts))
            return false;
        var delta = Math.Abs(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - ts);
        if (delta > ReplayWindowSeconds)
            return false;

        // Compute expected signature.
        var baseString  = $"v0:{timestampHeader}:{rawBody}";
        var secretBytes = Encoding.UTF8.GetBytes(signingSecret);
        var baseBytes   = Encoding.UTF8.GetBytes(baseString);
        using var hmac  = new HMACSHA256(secretBytes);
        var hashBytes   = hmac.ComputeHash(baseBytes);
        var expected    = "v0=" + Convert.ToHexString(hashBytes).ToLowerInvariant();

        // Constant-time compare — both values are always fixed-length hex strings.
        var expectedBytes  = Encoding.UTF8.GetBytes(expected);
        var signatureBytes = Encoding.UTF8.GetBytes(signatureHeader);
        if (expectedBytes.Length != signatureBytes.Length)
            return false;
        return CryptographicOperations.FixedTimeEquals(expectedBytes, signatureBytes);
    }
}
