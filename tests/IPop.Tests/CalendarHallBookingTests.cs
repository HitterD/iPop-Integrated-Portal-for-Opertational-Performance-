using IPop.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;
using static IPop.Api.Endpoints.CalendarEndpoints;

namespace IPop.Tests;

/// QA 2026-10-12 (Meeting Rooms task 13): the Calendar form saves through /api/calendar/events, which never checked
/// SJA Hall double bookings, and a room booked without invitees stayed a personal event the room card ignored.
public class CalendarHallBookingTests
{
    [Fact]
    public async Task Booking_a_hall_room_without_invitees_is_a_meeting_the_room_card_sees()
    {
        using var t = new TestDb(); var a = t.AddUser("A1");
        var start = SafeBookingTime.Start;
        var (r, ev, _) = await CreateSeriesAsync(t.Db, a.Id, new SaveEventRequest("Toraja standup", start, start.AddHours(1), Location: "sja hall toraja"), default);
        Assert.Equal(SaveResult.Ok, r);
        Assert.NotNull(ev!.MeetingId);
        var m = await t.Verify().Meetings.SingleAsync();
        Assert.Equal("SJA Hall · Toraja", m.Location);
        Assert.Equal(a.Id, m.OrganizerId);
    }

    [Fact]
    public async Task Overlapping_calendar_bookings_of_the_same_room_are_refused_on_create_and_update()
    {
        using var t = new TestDb(); var a = t.AddUser("A1"); var b = t.AddUser("B1");
        var start = SafeBookingTime.Start;
        Assert.Equal(SaveResult.Ok, (await CreateSeriesAsync(t.Db, a.Id, new SaveEventRequest("First", start, start.AddHours(1), Location: "SJA Hall · Toraja"), default)).Item1);

        // Solo and with invitees, single and repeating: all clash.
        Assert.Equal(SaveResult.RoomBooked, (await CreateSeriesAsync(t.Verify(), b.Id, new SaveEventRequest("Solo", start.AddMinutes(30), start.AddHours(2), Location: "SJA Hall · Toraja"), default)).Item1);
        Assert.Equal(SaveResult.RoomBooked, (await CreateSeriesAsync(t.Verify(), b.Id, new SaveEventRequest("Team", start, start.AddHours(1), Location: "SJA Hall · Toraja", AttendeeIds: [a.Id]), default)).Item1);
        Assert.Equal(SaveResult.RoomBooked, (await CreateSeriesAsync(t.Verify(), b.Id, new SaveEventRequest("Daily", start, start.AddHours(1), Location: "SJA Hall · Toraja", Recurrence: "D;C:3"), default)).Item1);
        Assert.Single(await t.Verify().Meetings.ToListAsync());

        // Another room, a normal place and back-to-back are fine.
        Assert.Equal(SaveResult.Ok, (await CreateSeriesAsync(t.Verify(), b.Id, new SaveEventRequest("Other room", start, start.AddHours(1), Location: "SJA Hall · Kalosi"), default)).Item1);
        Assert.Equal(SaveResult.Ok, (await CreateSeriesAsync(t.Verify(), b.Id, new SaveEventRequest("Next", start.AddHours(1), start.AddHours(2), Location: "SJA Hall · Toraja"), default)).Item1);
        var (_, personal, _) = await CreateSeriesAsync(t.Verify(), b.Id, new SaveEventRequest("Desk", start, start.AddHours(1), Location: "Lantai 3"), default);
        Assert.Null(personal!.MeetingId);

        // Moving a personal event or a meeting into the booked slot is refused (not reported as a bad time range).
        Assert.Equal(SaveResult.RoomBooked, (await UpdateEventAsync(t.Verify(), personal.Id, b.Id, new SaveEventRequest("Desk", start, start.AddHours(1), Location: "SJA Hall · Toraja"), default)).Item1);
        var kalosi = await t.Verify().CalendarEvents.SingleAsync(e => e.Title == "Other room");
        Assert.Equal(SaveResult.RoomBooked, (await UpdateEventAsync(t.Verify(), kalosi.Id, b.Id, new SaveEventRequest("Other room", start, start.AddHours(1), Location: "SJA Hall · Toraja"), default)).Item1);
        // Editing the booking itself does not clash with itself.
        var first = await t.Verify().CalendarEvents.SingleAsync(e => e.Title == "First");
        Assert.Equal(SaveResult.Ok, (await UpdateEventAsync(t.Verify(), first.Id, a.Id, new SaveEventRequest("First (renamed)", start, start.AddHours(1), Location: "SJA Hall · Toraja"), default)).Item1);
    }
}
