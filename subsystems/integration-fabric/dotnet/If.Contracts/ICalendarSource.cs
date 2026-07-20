namespace If.Contracts;

/// <summary>Retrieves calendar events for a given time window on behalf of a signed-in user.</summary>
public interface ICalendarSource
{
    Task<IReadOnlyList<CalendarArtifact>> GetEventsAsync(
        string signedInUserPrincipalId,
        CalendarWindow window,
        CancellationToken ct);
}
