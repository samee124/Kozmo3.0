namespace Po.VendorCall;

/// <summary>
/// Deterministic scorer that decides whether a calendar event represents a vendor call.
/// No LLM, no clock reads, no I/O — pure function over its inputs.
/// </summary>
public sealed class VendorCallRecognizer
{
    private readonly VendorCallRecognitionConfig _config;

    public VendorCallRecognizer(VendorCallRecognitionConfig config)
        => _config = config;

    /// <summary>
    /// Scores an event and returns a recognition result.
    /// </summary>
    /// <param name="title">Event subject / title.</param>
    /// <param name="body">Event body preview (may be empty).</param>
    /// <param name="attendeeEmails">All attendee email addresses including the organizer.</param>
    public VendorCallRecognitionResult Recognize(
        string                title,
        string                body,
        IReadOnlyList<string> attendeeEmails)
    {
        var external = attendeeEmails
            .Where(e => !IsInternal(e, _config.InternalDomains))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (external.Count == 0)
            return new VendorCallRecognitionResult(
                IsRelevant:        false,
                RequiresReview:    false,
                Confidence:        1.0,
                ExternalAttendees: [],
                MatchedTitleTerms: [],
                MatchedBodyTerms:  []);

        var titleLower = (title ?? "").ToLowerInvariant();
        var bodyLower  = (body  ?? "").ToLowerInvariant();

        var matchedTitle = _config.TitleTerms
            .Where(t => titleLower.Contains(t, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var matchedBody = _config.BodyTerms
            .Where(t => bodyLower.Contains(t, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Title bonus: +0.2 for first match, +0.1 per additional, capped at 0.3 total.
        double titleBonus = matchedTitle.Count >= 1
            ? Math.Min(0.2 + (matchedTitle.Count - 1) * 0.1, 0.3)
            : 0.0;

        // Body bonus: +0.05 per match, capped at 0.15 total.
        double bodyBonus = Math.Min(matchedBody.Count * 0.05, 0.15);

        double confidence   = Math.Min(1.0, 0.5 + titleBonus + bodyBonus);
        bool   isRelevant   = confidence >= _config.ReviewRelevantThreshold;
        bool   requiresReview = isRelevant && confidence < _config.AutoRelevantThreshold;

        return new VendorCallRecognitionResult(
            IsRelevant:        isRelevant,
            RequiresReview:    requiresReview,
            Confidence:        confidence,
            ExternalAttendees: external,
            MatchedTitleTerms: matchedTitle,
            MatchedBodyTerms:  matchedBody);
    }

    private static bool IsInternal(string email, IReadOnlyList<string> internalDomains)
    {
        var at = email.IndexOf('@');
        if (at < 0) return true; // malformed → treat as internal
        var domain = email[(at + 1)..];
        return internalDomains.Any(d => d.Equals(domain, StringComparison.OrdinalIgnoreCase));
    }
}
