namespace If.Contracts;

/// <summary>Time window used to bound calendar event retrieval.</summary>
public sealed record CalendarWindow(DateTimeOffset FromUtc, DateTimeOffset ToUtc);
