using Bunit;
using IPop.Web.Components.Calendar;
using Xunit;

namespace IPop.Tests;

public class MeetingRoomCalendarTests
{
    [Theory]
    [InlineData(409, "That SJA Hall room is already booked at this time.")]
    [InlineData(422, "Invalid meeting time.")]
    public async Task Calendar_form_preserves_api_error_message(int status, string message)
    {
        using var response = new HttpResponseMessage((System.Net.HttpStatusCode)status)
        {
            Content = System.Net.Http.Json.JsonContent.Create(new { code = "room_booked", message })
        };
        var method = typeof(IPop.Web.Pages.Calendar).GetMethod("ErrorAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        var result = await (Task<string>)method.Invoke(null, [response])!;
        Assert.Equal(message, result);
    }

    [Theory]
    [InlineData("scheduled")]
    [InlineData(null)]
    public void Hall_room_has_join_link_when_not_cancelled(string? status)
    {
        using var ctx = new BunitContext();
        var cut = ctx.Render<CalendarRoomJoin>(p => p
            .Add(c => c.Location, "SJA Hall · Kalosi")
            .Add(c => c.MeetingStatus, status));
        var link = cut.Find("a.cal-btn.primary");
        Assert.Equal("/calls?tab=rooms&join=kalosi", link.GetAttribute("href"));
        Assert.Equal("Join room", link.TextContent);
        Assert.Equal("door-open", cut.Find(".cal-detail-row i").GetAttribute("data-lucide"));
    }

    [Theory]
    [InlineData("Lantai 3", "scheduled")]
    [InlineData("SJA Hall · Kalosi", "cancelled")]
    [InlineData(null, "scheduled")]
    public void Other_locations_and_cancelled_meetings_have_no_join_link(string? location, string? status)
    {
        using var ctx = new BunitContext();
        var cut = ctx.Render<CalendarRoomJoin>(p => p
            .Add(c => c.Location, location)
            .Add(c => c.MeetingStatus, status));
        Assert.Empty(cut.FindAll("a"));
    }
}
