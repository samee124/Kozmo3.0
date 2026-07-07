using System.Security.Cryptography;
using System.Text;

namespace Wc.CheckIn;

/// <summary>
/// Generates and validates signed capability tokens for check-in quick-answer email links.
///
/// Payload format (UTF-8): "{checkInId:D}|{value}|{expiresAt:O}"
/// Token format: Base64Url(payload) + "." + Base64Url(HMACSHA256(payload, secretBytes))
///
/// TryValidate uses constant-time comparison (CryptographicOperations.FixedTimeEquals) to
/// prevent timing attacks. The GET path that validates these tokens NEVER mutates state —
/// mutation happens only on POST, which re-validates the token independently.
/// </summary>
public static class CheckInLinkToken
{
    /// <summary>
    /// Generates a signed token embedding the check-in ID, answer value, and expiry timestamp.
    /// </summary>
    public static string Generate(Guid checkInId, string value, string secret, int ttlDays, DateTimeOffset now)
    {
        var expiresAt   = now.AddDays(ttlDays).ToString("O");
        var payload     = $"{checkInId:D}|{value}|{expiresAt}";
        var payloadB64  = ToBase64Url(Encoding.UTF8.GetBytes(payload));
        var sig         = ComputeSignature(payload, secret);
        return $"{payloadB64}.{sig}";
    }

    /// <summary>
    /// Validates a token. Populates <paramref name="checkInId"/> and <paramref name="value"/> on success.
    /// Returns false if the token is malformed, the signature does not match, or the token is expired.
    /// </summary>
    public static bool TryValidate(
        string         token,
        string         secret,
        DateTimeOffset now,
        out Guid       checkInId,
        out string     value)
    {
        checkInId = Guid.Empty;
        value     = string.Empty;

        if (string.IsNullOrWhiteSpace(token)) return false;

        var dot = token.IndexOf('.');
        if (dot < 0 || dot == token.Length - 1) return false;

        var payloadB64 = token[..dot];
        var sigB64     = token[(dot + 1)..];

        byte[] payloadBytes;
        try   { payloadBytes = FromBase64Url(payloadB64); }
        catch { return false; }

        var payload = Encoding.UTF8.GetString(payloadBytes);

        // Constant-time signature comparison prevents timing attacks.
        var expectedSig = ComputeSignature(payload, secret);
        if (!ConstantTimeEquals(sigB64, expectedSig)) return false;

        // Parse payload: "{checkInId}|{value}|{expiresAt:O}"
        var parts = payload.Split('|');
        if (parts.Length != 3) return false;

        if (!Guid.TryParse(parts[0], out checkInId)) return false;

        value = parts[1];

        if (!DateTimeOffset.TryParse(
                parts[2], null,
                System.Globalization.DateTimeStyles.RoundtripKind,
                out var expiresAt))
            return false;

        if (now > expiresAt) return false;

        return true;
    }

    // ── private helpers ─────────────────────────────────────────────────────

    private static string ComputeSignature(string payload, string secret)
    {
        var secretBytes  = Encoding.UTF8.GetBytes(secret);
        var payloadBytes = Encoding.UTF8.GetBytes(payload);
        var hash         = HMACSHA256.HashData(secretBytes, payloadBytes);
        return ToBase64Url(hash);
    }

    private static string ToBase64Url(byte[] data)
        => Convert.ToBase64String(data)
                  .Replace('+', '-').Replace('/', '_').TrimEnd('=');

    private static byte[] FromBase64Url(string b64)
    {
        var s = b64.Replace('-', '+').Replace('_', '/');
        s += (s.Length % 4) switch { 2 => "==", 3 => "=", _ => "" };
        return Convert.FromBase64String(s);
    }

    private static bool ConstantTimeEquals(string a, string b)
    {
        var aBytes = Encoding.UTF8.GetBytes(a);
        var bBytes = Encoding.UTF8.GetBytes(b);
        return CryptographicOperations.FixedTimeEquals(aBytes, bBytes);
    }
}
