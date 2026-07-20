using System.Globalization;
using If.Contracts;
using Microsoft.Graph.Models;

namespace If.MicrosoftGraph;

/// <summary>Maps Microsoft Graph calendar event responses to CalendarArtifact records.</summary>
public sealed class GraphCalendarMapper
{
    /// <summary>Maps a Graph Event to a CalendarArtifact. Never throws on null Graph fields.</summary>
    public static CalendarArtifact Map(
        Event ev,
        string tenantId,
        string signedInUserPrincipalId)
        => new(
            ArtifactId:          Guid.NewGuid(),
            SourceSystem:        "microsoft_graph",
            SourceType:          "calendar_event",
            TenantId:            tenantId,
            SourcePrincipalId:   signedInUserPrincipalId,
            ExternalId:          $"msgraph:event:{ev.Id ?? ""}",
            ICalUid:             ev.ICalUId ?? "",
            Subject:             ev.Subject ?? "",
            StartUtc:            ParseDateTimeUtc(ev.Start),
            EndUtc:              ParseDateTimeUtc(ev.End),
            Organizer:           ev.Organizer?.EmailAddress?.Address ?? "",
            Attendees:           MapAttendees(ev.Attendees),
            BodyPreview:         ev.BodyPreview ?? "",
            CapturedAtUtc:       DateTimeOffset.UtcNow,
            JoinWebUrl:          ev.OnlineMeeting?.JoinUrl);

    private static DateTimeOffset ParseDateTimeUtc(DateTimeTimeZone? dt)
    {
        if (dt?.DateTime is null)
            return DateTimeOffset.UtcNow;

        var parsed = DateTime.Parse(dt.DateTime, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

        if (string.IsNullOrEmpty(dt.TimeZone) || dt.TimeZone == "UTC")
            return new DateTimeOffset(parsed, TimeSpan.Zero);

        try
        {
            var tz  = TimeZoneInfo.FindSystemTimeZoneById(dt.TimeZone);
            var utc = TimeZoneInfo.ConvertTimeToUtc(
                DateTime.SpecifyKind(parsed, DateTimeKind.Unspecified), tz);
            return new DateTimeOffset(utc, TimeSpan.Zero);
        }
        catch
        {
            // Unknown timezone ID — treat parsed value as UTC
            return new DateTimeOffset(parsed, TimeSpan.Zero);
        }
    }

    private static IReadOnlyList<string> MapAttendees(List<Attendee>? attendees)
    {
        if (attendees is null or { Count: 0 })
            return [];

        return attendees
            .Select(a => a.EmailAddress?.Address)
            .Where(addr => !string.IsNullOrEmpty(addr))
            .Select(addr => addr!)
            .ToList();
    }
}
