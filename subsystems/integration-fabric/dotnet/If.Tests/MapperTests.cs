using If.MicrosoftGraph;
using Microsoft.Graph.Models;
using Xunit;

namespace If.Tests;

public sealed class MapperTests
{
    // ── Fixtures ─────────────────────────────────────────────────────────────

    private static Event FullEvent(
        string id          = "AAMkAG001",
        string iCalUid     = "ical-uid-001",
        string subject     = "Board meeting",
        string startDt     = "2026-07-16T09:00:00.0000000",
        string endDt       = "2026-07-16T10:00:00.0000000",
        string tz          = "UTC",
        string organizer   = "ceo@contoso.com",
        string bodyPreview = "Agenda: Q2 review") => new Event
    {
        Id          = id,
        ICalUId     = iCalUid,
        Subject     = subject,
        Start       = new DateTimeTimeZone { DateTime = startDt, TimeZone = tz },
        End         = new DateTimeTimeZone { DateTime = endDt,   TimeZone = tz },
        Organizer   = new Recipient { EmailAddress = new EmailAddress { Address = organizer } },
        Attendees   =
        [
            new Attendee { EmailAddress = new EmailAddress { Address = "alice@contoso.com" } },
            new Attendee { EmailAddress = new EmailAddress { Address = "bob@contoso.com" } },
        ],
        BodyPreview = bodyPreview,
    };

    // ── Full mapping ──────────────────────────────────────────────────────────

    [Fact]
    public void Map_FullyPopulatedEvent_MapsAllFieldsCorrectly()
    {
        var artifact = GraphCalendarMapper.Map(FullEvent(), "tenant-001", "upn@contoso.com");

        Assert.Equal("microsoft_graph",     artifact.SourceSystem);
        Assert.Equal("calendar_event",      artifact.SourceType);
        Assert.Equal("tenant-001",          artifact.TenantId);
        Assert.Equal("upn@contoso.com",     artifact.SourcePrincipalId);
        Assert.Equal("ical-uid-001",        artifact.ICalUid);
        Assert.Equal("Board meeting",       artifact.Subject);
        Assert.Equal("ceo@contoso.com",     artifact.Organizer);
        Assert.Equal(2,                     artifact.Attendees.Count);
        Assert.Contains("alice@contoso.com", artifact.Attendees);
        Assert.Contains("bob@contoso.com",  artifact.Attendees);
        Assert.Equal("Agenda: Q2 review",   artifact.BodyPreview);
        Assert.NotEqual(Guid.Empty,         artifact.ArtifactId);
    }

    // ── ExternalId format ─────────────────────────────────────────────────────

    [Fact]
    public void Map_ExternalId_HasMsgraphEventPrefix()
    {
        var artifact = GraphCalendarMapper.Map(FullEvent(id: "AAMkAG999"), "t", "u");
        Assert.StartsWith("msgraph:event:", artifact.ExternalId);
        Assert.Equal("msgraph:event:AAMkAG999", artifact.ExternalId);
    }

    [Fact]
    public void Map_ExternalId_NullEventId_ProducesEmptySuffix()
    {
        var ev = FullEvent();
        ev.Id  = null;
        var artifact = GraphCalendarMapper.Map(ev, "t", "u");
        Assert.Equal("msgraph:event:", artifact.ExternalId);
    }

    // ── Null organizer ────────────────────────────────────────────────────────

    [Fact]
    public void Map_NullOrganizer_DoesNotThrow_ReturnsEmptyString()
    {
        var ev = FullEvent();
        ev.Organizer = null;
        var artifact = GraphCalendarMapper.Map(ev, "t", "u");
        Assert.Equal("", artifact.Organizer);
    }

    [Fact]
    public void Map_OrganizerWithNullEmailAddress_DoesNotThrow()
    {
        var ev = FullEvent();
        ev.Organizer = new Recipient { EmailAddress = null };
        var artifact = GraphCalendarMapper.Map(ev, "t", "u");
        Assert.Equal("", artifact.Organizer);
    }

    // ── Attendees ─────────────────────────────────────────────────────────────

    [Fact]
    public void Map_NullAttendees_DoesNotThrow_ReturnsEmptyList()
    {
        var ev = FullEvent();
        ev.Attendees = null;
        var artifact = GraphCalendarMapper.Map(ev, "t", "u");
        Assert.NotNull(artifact.Attendees);
        Assert.Empty(artifact.Attendees);
    }

    [Fact]
    public void Map_EmptyAttendeeList_ReturnsEmptyList_NotNull()
    {
        var ev = FullEvent();
        ev.Attendees = [];
        var artifact = GraphCalendarMapper.Map(ev, "t", "u");
        Assert.NotNull(artifact.Attendees);
        Assert.Empty(artifact.Attendees);
    }

    [Fact]
    public void Map_AttendeeWithNullEmail_IsSkipped()
    {
        var ev = FullEvent();
        ev.Attendees =
        [
            new Attendee { EmailAddress = new EmailAddress { Address = "valid@contoso.com" } },
            new Attendee { EmailAddress = null },
            new Attendee { EmailAddress = new EmailAddress { Address = null } },
        ];
        var artifact = GraphCalendarMapper.Map(ev, "t", "u");
        var addr = Assert.Single(artifact.Attendees);
        Assert.Equal("valid@contoso.com", addr);
    }

    // ── BodyPreview ───────────────────────────────────────────────────────────

    [Fact]
    public void Map_NullBodyPreview_DoesNotThrow_ReturnsEmptyString()
    {
        var ev = FullEvent();
        ev.BodyPreview = null;
        var artifact = GraphCalendarMapper.Map(ev, "t", "u");
        Assert.Equal("", artifact.BodyPreview);
    }

    // ── DateTime / UTC handling ───────────────────────────────────────────────

    [Fact]
    public void Map_StartUtc_IsUtcOffset_WhenTimeZoneIsUTC()
    {
        var artifact = GraphCalendarMapper.Map(
            FullEvent(startDt: "2026-07-16T09:00:00.0000000", tz: "UTC"), "t", "u");

        Assert.Equal(TimeSpan.Zero, artifact.StartUtc.Offset);
        Assert.Equal(9,             artifact.StartUtc.Hour);
    }

    [Fact]
    public void Map_EndUtc_IsUtcOffset_WhenTimeZoneIsUTC()
    {
        var artifact = GraphCalendarMapper.Map(
            FullEvent(endDt: "2026-07-16T10:30:00.0000000", tz: "UTC"), "t", "u");

        Assert.Equal(TimeSpan.Zero, artifact.EndUtc.Offset);
        Assert.Equal(10,            artifact.EndUtc.Hour);
        Assert.Equal(30,            artifact.EndUtc.Minute);
    }

    [Fact]
    public void Map_StartUtc_ConvertsToUtc_WhenTimeZoneIsNonUtc()
    {
        // "Eastern Standard Time" = UTC-5 (EST) or UTC-4 (EDT in summer)
        // 09:00 on 2026-07-16 is EDT (UTC-4) → 13:00 UTC
        var ev = new Event
        {
            Id      = "x",
            Subject = "test",
            Start   = new DateTimeTimeZone { DateTime = "2026-07-16T09:00:00.0000000", TimeZone = "Eastern Standard Time" },
            End     = new DateTimeTimeZone { DateTime = "2026-07-16T10:00:00.0000000", TimeZone = "Eastern Standard Time" },
        };
        var artifact = GraphCalendarMapper.Map(ev, "t", "u");

        // Must be returned as UTC (zero offset)
        Assert.Equal(TimeSpan.Zero, artifact.StartUtc.Offset);
        // Must have been converted — 09:00 local is NOT 09:00 UTC
        Assert.NotEqual(9, artifact.StartUtc.UtcDateTime.Hour);
    }

    [Fact]
    public void Map_NullStart_DoesNotThrow()
    {
        var ev = FullEvent();
        ev.Start = null;
        var artifact = GraphCalendarMapper.Map(ev, "t", "u");
        // Should return a sensible default (UtcNow), not throw
        Assert.True(artifact.StartUtc > DateTimeOffset.MinValue);
    }

    // ── JoinWebUrl ────────────────────────────────────────────────────────────

    [Fact]
    public void Map_WithOnlineMeeting_PopulatesJoinWebUrl()
    {
        var ev = FullEvent();
        ev.OnlineMeeting = new OnlineMeetingInfo { JoinUrl = "https://teams.microsoft.com/l/meetup-join/xxx" };
        var artifact = GraphCalendarMapper.Map(ev, "t", "u");
        Assert.Equal("https://teams.microsoft.com/l/meetup-join/xxx", artifact.JoinWebUrl);
    }

    [Fact]
    public void Map_NoOnlineMeeting_JoinWebUrlIsNull()
    {
        var artifact = GraphCalendarMapper.Map(FullEvent(), "t", "u");
        Assert.Null(artifact.JoinWebUrl);
    }
}
