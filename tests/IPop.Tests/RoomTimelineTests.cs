using Bunit;
using IPop.Application.MeetingRooms;
using IPop.Web.Components.Meetings;
using IPop.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IPop.Tests;

/// Meeting scheduling v3 §2 (mockup screen 2): the room day timeline — block geometry, hidden titles, clash and Try.
[Collection("AvatarPhotos")]
public class RoomTimelineTests
{
    private static readonly TimeZoneInfo Wib = TimeZoneInfo.CreateCustomTimeZone("WIB", TimeSpan.FromHours(7), "WIB", "WIB");
    private static readonly DateOnly Day = new(2026, 10, 13);
    /// Local WIB time on Day → UTC.
    private static DateTime At(int h, int m = 0) => new DateTime(2026, 10, 13, h, m, 0, DateTimeKind.Utc).AddHours(-7);
    /// 09:50 local, like the mockup.
    private static readonly DateTime NowUtc = At(9, 50);

    private static RoomDayDto Kalosi(params RoomBlockDto[] blocks) =>
        new("kalosi", "Kalosi", 20, Day, At(7), At(19), blocks);

    private static RoomBlockDto Block(string title, DateTime s, DateTime e, bool visible = true) =>
        new(visible ? Guid.NewGuid() : null, visible ? title : "Booked", s, e, false, visible);

    private static RoomDayDto Mockup() => Kalosi(
        Block("Morning huddle", At(7, 30), At(8, 30)),
        Block("Weekly ICT", At(10), At(11)),
        Block("Finance sync", At(15, 45), At(17), visible: false));

    private static IRenderedComponent<RoomTimeline> Render(BunitContext ctx, RoomDayDto day, DateTime s, DateTime e,
        Action<(DateTime, DateTime)>? onChange = null) =>
        ctx.Render<RoomTimeline>(p => p.Add(c => c.Day, day).Add(c => c.Start, s).Add(c => c.End, e).Add(c => c.Accent, "r-kalosi")
            .Add(c => c.Now, NowUtc).Add(c => c.Zone, Wib)
            .Add(c => c.OnChange, v => onChange?.Invoke(v)));

    [Fact]
    public void Blocks_and_selection_are_positioned_by_percent_of_the_window()
    {
        using var ctx = Context();
        var cut = Render(ctx, Mockup(), At(13), At(15, 30));
        var busy = cut.FindAll(".tl-busy");
        Assert.Equal(3, busy.Count);
        // 07:30–08:30 in a 07:00–19:00 window (720 min): left 30/720, width 60/720.
        Assert.Equal("left:4.167%;width:8.333%", busy[0].GetAttribute("style"));
        Assert.Equal("left:25%;width:8.333%", busy[1].GetAttribute("style"));
        var sel = cut.Find(".tl-sel");
        Assert.Equal("left:50%;width:20.833%", sel.GetAttribute("style"));
        Assert.Equal("13:00–15:30", sel.QuerySelector(".tl-lab")!.TextContent);
        Assert.DoesNotContain("bad", sel.ClassList);
        Assert.Empty(cut.FindAll(".tl-clash"));
        // now line + past shade at 09:50 = 170/720
        Assert.Equal("left:23.611%", cut.Find(".tl-now").GetAttribute("style"));
        Assert.Equal("width:23.611%", cut.Find(".tl-past").GetAttribute("style"));
        Assert.Equal(["07:00", "08:00", "09:00", "10:00", "11:00", "12:00", "13:00", "14:00", "15:00", "16:00", "17:00", "18:00", "19:00"],
            cut.FindAll(".tl-hours span").Select(s => s.TextContent).ToArray());
        Assert.Contains("Kalosi today", cut.Find(".tl-h b").TextContent);
    }

    [Fact]
    public void Hidden_titles_show_Booked_and_never_the_title()
    {
        using var ctx = Context();
        var cut = Render(ctx, Mockup(), At(13), At(14));
        Assert.Equal("Booked", cut.FindAll(".tl-busy")[2].GetAttribute("data-title"));
        Assert.Contains("Booked15:45–17:00", cut.Find(".tl-tags").TextContent);
        Assert.DoesNotContain("Finance sync", cut.Markup);
        // Past bookings get no tag above the track.
        Assert.DoesNotContain("Morning huddle", cut.Find(".tl-tags").TextContent);
    }

    [Fact]
    public void Clash_marks_the_block_and_offers_the_next_free_slot_of_the_same_length()
    {
        using var ctx = Context();
        (DateTime S, DateTime E)? changed = null;
        var cut = Render(ctx, Mockup(), At(10, 30), At(12), v => changed = v);
        Assert.Contains("bad", cut.Find(".tl-sel").ClassList);
        var clash = cut.Find(".tl-clash");
        Assert.Contains("Clashes with Weekly ICT 10:00–11:00", clash.TextContent);
        Assert.Contains("Kalosi is booked then. Pick another time or another room.", clash.TextContent);
        Assert.Equal("Try 11:00–12:30", cut.Find(".tl-try").TextContent.Trim());

        cut.Find(".tl-try").Click();
        Assert.Equal((At(11), At(12, 30)), changed);
    }

    [Fact]
    public void Clash_with_a_hidden_booking_says_Booked_and_skips_past_it()
    {
        using var ctx = Context();
        var cut = Render(ctx, Mockup(), At(15), At(16));
        Assert.Contains("Clashes with Booked 15:45–17:00", cut.Find(".tl-clash").TextContent);
        Assert.Equal("Try 17:00–18:00", cut.Find(".tl-try").TextContent.Trim());
    }

    [Fact]
    public void No_Try_when_nothing_of_that_length_fits_before_the_window_ends()
    {
        using var ctx = Context();
        var cut = Render(ctx, Kalosi(Block("Board", At(12), At(18, 30))), At(12), At(13));
        Assert.NotNull(cut.Find(".tl-clash"));
        Assert.Empty(cut.FindAll(".tl-try"));
    }

    [Fact]
    public void Back_to_back_is_not_a_clash()
    {
        using var ctx = Context();
        var cut = Render(ctx, Mockup(), At(11), At(12));
        Assert.Empty(cut.FindAll(".tl-clash"));
    }

    [Fact]
    public void Drag_from_js_reports_the_snapped_minutes_as_utc()
    {
        using var ctx = Context();
        (DateTime S, DateTime E)? changed = null;
        var cut = Render(ctx, Mockup(), At(13), At(14), v => changed = v);
        cut.InvokeAsync(() => cut.Instance.OnDrag(13 * 60 + 15, 14 * 60 + 45, false));
        Assert.Equal((At(13, 15), At(14, 45)), changed);
    }

    [Fact]
    public void Another_day_has_no_now_line()
    {
        using var ctx = Context();
        var day = new RoomDayDto("kalosi", "Kalosi", 20, Day.AddDays(1), At(7).AddDays(1), At(19).AddDays(1), []);
        var cut = Render(ctx, day, At(13).AddDays(1), At(14).AddDays(1));
        Assert.Empty(cut.FindAll(".tl-now"));
        Assert.Empty(cut.FindAll(".tl-past"));
        Assert.Contains("Kalosi tomorrow", cut.Find(".tl-h b").TextContent);
    }

    [Fact]
    public void NextSlot_mirrors_the_server_rule()
    {
        var blocks = Mockup().Blocks;
        Assert.Equal((At(11), At(12, 30)), MeetingKinds.NextSlot(blocks, At(10, 30), At(12), At(19), NowUtc));
        Assert.Equal((At(13), At(14)), MeetingKinds.NextSlot(blocks, At(13), At(14), At(19), NowUtc));   // fits: itself
        Assert.Null(MeetingKinds.NextSlot(blocks, At(15), At(18, 30), At(19), NowUtc));
        // A clash with a booking that ended before now: the slot starts now, not in the past.
        Assert.Equal((At(9, 50), At(10)), MeetingKinds.NextSlot(blocks, At(8), At(8, 10), At(19), NowUtc));
    }

    private static BunitContext Context()
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddScoped<PhotoViewerState>();
        return ctx;
    }
}
